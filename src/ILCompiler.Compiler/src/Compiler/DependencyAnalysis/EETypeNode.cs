// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;

using Internal.IL;
using Internal.Runtime;
using Internal.Text;
using Internal.TypeSystem;

using Debug = System.Diagnostics.Debug;
using GenericVariance = Internal.Runtime.GenericVariance;

namespace ILCompiler.DependencyAnalysis
{
    /// <summary>
    /// Given a type, EETypeNode writes an EEType data structure in the format expected by the runtime.
    /// 
    /// Format of an EEType:
    /// 
    /// Field Size      | Contents
    /// ----------------+-----------------------------------
    /// UInt16          | Component Size. For arrays this is the element type size, for strings it is 2 (.NET uses 
    ///                 | UTF16 character encoding), for generic type definitions it is the number of generic parameters,
    ///                 | and 0 for all other types.
    ///                 |
    /// UInt16          | EETypeKind (Normal, Array, Pointer type). Flags for: IsValueType, IsCrossModule, HasPointers,
    ///                 | HasOptionalFields, IsInterface, IsGeneric. Top 5 bits are used for enum EETypeElementType to
    ///                 | record whether it's back by an Int32, Int16 etc
    ///                 |
    /// Uint32          | Base size.
    ///                 |
    /// [Pointer Size]  | Related type. Base type for regular types. Element type for arrays / pointer types.
    ///                 |
    /// UInt16          | Number of VTable slots (X)
    ///                 |
    /// UInt16          | Number of interfaces implemented by type (Y)
    ///                 |
    /// UInt32          | Hash code
    ///                 |
    /// [Pointer Size]  | Pointer to containing TypeManager indirection cell
    ///                 |
    /// X * [Ptr Size]  | VTable entries (optional)
    ///                 |
    /// Y * [Ptr Size]  | Pointers to interface map data structures (optional)
    ///                 |
    /// [Pointer Size]  | Pointer to finalizer method (optional)
    ///                 |
    /// [Pointer Size]  | Pointer to optional fields (optional)
    ///                 |
    /// [Pointer Size]  | Pointer to the generic type definition EEType (optional)
    ///                 |
    /// [Pointer Size]  | Pointer to the generic argument and variance info (optional)
    /// </summary>
    public partial class EETypeNode : ObjectNode, IExportableSymbolNode, IEETypeNode, ISymbolDefinitionNode, ISymbolNodeWithLinkage
    {
        protected TypeDesc _type;
        internal EETypeOptionalFieldsBuilder _optionalFieldsBuilder = new EETypeOptionalFieldsBuilder();
        internal EETypeOptionalFieldsNode _optionalFieldsNode;

        public EETypeNode(NodeFactory factory, TypeDesc type)
        {
            if (type.IsCanonicalDefinitionType(CanonicalFormKind.Any))
                Debug.Assert(this is CanonicalDefinitionEETypeNode);
            else if (type.IsCanonicalSubtype(CanonicalFormKind.Any))
                Debug.Assert((this is CanonicalEETypeNode) || (this is NecessaryCanonicalEETypeNode));

            Debug.Assert(!type.IsRuntimeDeterminedSubtype);
            _type = type;
            _optionalFieldsNode = new EETypeOptionalFieldsNode(this);

            factory.TypeSystemContext.EnsureLoadableType(type);
        }

        protected override string GetName(NodeFactory factory) => this.GetMangledName(factory.NameMangler);

        public override bool ShouldSkipEmittingObjectNode(NodeFactory factory)
        {
            // If there is a constructed version of this node in the graph, emit that instead
            if (ConstructedEETypeNode.CreationAllowed(_type))
                return factory.ConstructedTypeSymbol(_type).Marked;

            return false;
        }

        public virtual ISymbolNode NodeForLinkage(NodeFactory factory)
        {
            return factory.NecessaryTypeSymbol(_type);
        }

        public ExportForm GetExportForm(NodeFactory factory) => factory.CompilationModuleGroup.GetExportTypeForm(Type);

        public TypeDesc Type => _type;

        public override ObjectNodeSection Section
        {
            get
            {
                if (_type.Context.Target.IsWindows)
                    return ObjectNodeSection.ReadOnlyDataSection;
                else
                    return ObjectNodeSection.DataSection;
            }
        }

        public int MinimumObjectSize => _type.Context.Target.PointerSize * 3;

        protected virtual bool EmitVirtualSlotsAndInterfaces => false;

        internal bool HasOptionalFields
        {
            get { return _optionalFieldsBuilder.IsAtLeastOneFieldUsed(); }
        }

        internal byte[] GetOptionalFieldsData()
        {
            return _optionalFieldsBuilder.GetBytes();
        }
        
        public override bool StaticDependenciesAreComputed => true;
        
        public static string GetMangledName(TypeDesc type, NameMangler nameMangler)
        {
            return nameMangler.NodeMangler.EEType(type);
        }

        public virtual void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append(nameMangler.NodeMangler.EEType(_type));
        }

        int ISymbolNode.Offset => 0;
        int ISymbolDefinitionNode.Offset => GCDescSize;

        public override bool IsShareable => IsTypeNodeShareable(_type);

        private bool CanonFormTypeMayExist
        {
            get
            {
                if (!_type.HasInstantiation)
                    return false;

                if (!_type.Context.SupportsCanon)
                    return false;

                // If type is already in canon form, a canonically equivalent type cannot exist
                if (_type.IsCanonicalSubtype(CanonicalFormKind.Any))
                    return false;

                // If we reach here, a universal canon variant can exist (if universal canon is supported)
                if (_type.Context.SupportsUniversalCanon)
                    return true;

                // Attempt to convert to canon. If the type changes, then the CanonForm exists
                return (_type.ConvertToCanonForm(CanonicalFormKind.Specific) != _type);
            }
        }

        public sealed override bool HasConditionalStaticDependencies
        {
            get
            {
                // If the type is can be converted to some interesting canon type, and this is the non-constructed variant of an EEType
                // we may need to trigger the fully constructed type to exist to make the behavior of the type consistent
                // in reflection and generic template expansion scenarios
                if (CanonFormTypeMayExist)
                {
                    return true;
                }

                if (!EmitVirtualSlotsAndInterfaces)
                    return false;

                // Since the vtable is dependency driven, generate conditional static dependencies for
                // all possible vtable entries
                foreach (var method in _type.GetClosestDefType().GetAllMethods())
                {
                    if (method.IsVirtual)
                        return true;
                }

                // If the type implements at least one interface, calls against that interface could result in this type's
                // implementation being used.
                if (_type.RuntimeInterfaces.Length > 0)
                    return true;

                return false;
            }
        }

        public sealed override IEnumerable<CombinedDependencyListEntry> GetConditionalStaticDependencies(NodeFactory factory)
        {
            IEETypeNode maximallyConstructableType = factory.MaximallyConstructableType(_type);

            if (maximallyConstructableType != this)
            {
                // EEType upgrading from necessary to constructed if some template instantation exists that matches up
                if (CanonFormTypeMayExist)
                {
                    yield return new CombinedDependencyListEntry(maximallyConstructableType, factory.MaximallyConstructableType(_type.ConvertToCanonForm(CanonicalFormKind.Specific)), "Trigger full type generation if canonical form exists");

                    if (_type.Context.SupportsUniversalCanon)
                        yield return new CombinedDependencyListEntry(maximallyConstructableType, factory.MaximallyConstructableType(_type.ConvertToCanonForm(CanonicalFormKind.Universal)), "Trigger full type generation if universal canonical form exists");
                }
                yield break;
            }

            if (!EmitVirtualSlotsAndInterfaces)
                yield break;

            DefType defType = _type.GetClosestDefType();

            // If we're producing a full vtable, none of the dependencies are conditional.
            if (!factory.VTable(defType).HasFixedSlots)
            {
                foreach (MethodDesc decl in defType.EnumAllVirtualSlots())
                {
                    // Generic virtual methods are tracked by an orthogonal mechanism.
                    if (decl.HasInstantiation)
                        continue;

                    MethodDesc impl = defType.FindVirtualFunctionTargetMethodOnObjectType(decl);
                    if (impl.OwningType == defType && !impl.IsAbstract)
                    {
                        MethodDesc canonImpl = impl.GetCanonMethodTarget(CanonicalFormKind.Specific);
                        yield return new CombinedDependencyListEntry(factory.MethodEntrypoint(canonImpl, _type.IsValueType), factory.VirtualMethodUse(decl), "Virtual method");
                    }
                }

                Debug.Assert(
                    _type == defType ||
                    ((System.Collections.IStructuralEquatable)defType.RuntimeInterfaces).Equals(_type.RuntimeInterfaces,
                    EqualityComparer<DefType>.Default));

                // Add conditional dependencies for interface methods the type implements. For example, if the type T implements
                // interface IFoo which has a method M1, add a dependency on T.M1 dependent on IFoo.M1 being called, since it's
                // possible for any IFoo object to actually be an instance of T.
                foreach (DefType interfaceType in defType.RuntimeInterfaces)
                {
                    Debug.Assert(interfaceType.IsInterface);

                    foreach (MethodDesc interfaceMethod in interfaceType.GetAllMethods())
                    {
                        if (interfaceMethod.Signature.IsStatic)
                            continue;

                        // Generic virtual methods are tracked by an orthogonal mechanism.
                        if (interfaceMethod.HasInstantiation)
                            continue;

                        MethodDesc implMethod = defType.ResolveInterfaceMethodToVirtualMethodOnType(interfaceMethod);
                        if (implMethod != null)
                        {
                            yield return new CombinedDependencyListEntry(factory.VirtualMethodUse(implMethod), factory.VirtualMethodUse(interfaceMethod), "Interface method");
                        }
                    }
                }
            }
        }

        public static bool IsTypeNodeShareable(TypeDesc type)
        {
            return type.IsParameterizedType || type.IsFunctionPointer || type is InstantiatedType;
        }

        private void AddVirtualMethodUseDependencies(DependencyList dependencyList, NodeFactory factory)
        {
            DefType closestDefType = _type.GetClosestDefType();

            if (_type.RuntimeInterfaces.Length > 0 && !factory.VTable(closestDefType).HasFixedSlots)
            {
                foreach (var implementedInterface in _type.RuntimeInterfaces)
                {
                    // If the type implements ICastable, the methods are implicitly necessary
                    if (implementedInterface == factory.ICastableInterface)
                    {
                        MethodDesc isInstDecl = implementedInterface.GetKnownMethod("IsInstanceOfInterface", null);
                        MethodDesc getImplTypeDecl = implementedInterface.GetKnownMethod("GetImplType", null);

                        MethodDesc isInstMethodImpl = _type.ResolveInterfaceMethodTarget(isInstDecl);
                        MethodDesc getImplTypeMethodImpl = _type.ResolveInterfaceMethodTarget(getImplTypeDecl);

                        if (isInstMethodImpl != null)
                            dependencyList.Add(factory.VirtualMethodUse(isInstMethodImpl), "ICastable IsInst");
                        if (getImplTypeMethodImpl != null)
                            dependencyList.Add(factory.VirtualMethodUse(getImplTypeMethodImpl), "ICastable GetImplType");
                    }

                    // If any of the implemented interfaces have variance, calls against compatible interface methods
                    // could result in interface methods of this type being used (e.g. IEnumberable<object>.GetEnumerator()
                    // can dispatch to an implementation of IEnumerable<string>.GetEnumerator()).
                    // For now, we will not try to optimize this and we will pretend all interface methods are necessary.
                    bool allInterfaceMethodsAreImplicitlyUsed = false;
                    if (implementedInterface.HasVariance)
                    {
                        TypeDesc interfaceDefinition = implementedInterface.GetTypeDefinition();
                        for (int i = 0; i < interfaceDefinition.Instantiation.Length; i++)
                        {
                            var variantParameter = (GenericParameterDesc)interfaceDefinition.Instantiation[i];
                            if (variantParameter.Variance != 0)
                            {
                                // Variant interface parameters that are instantiated over valuetypes are
                                // not actually variant. Neither are contravariant parameters instantiated
                                // over sealed types (there won't be another interface castable to it
                                // through variance on that parameter).
                                TypeDesc variantArgument = implementedInterface.Instantiation[i];
                                if (!variantArgument.IsValueType
                                    && (!variantArgument.IsSealed() || variantParameter.IsCovariant))
                                {
                                    allInterfaceMethodsAreImplicitlyUsed = true;
                                    break;
                                }
                            }
                        }
                    }
                    if (!allInterfaceMethodsAreImplicitlyUsed &&
                        (_type.IsArray || _type.GetTypeDefinition() == factory.ArrayOfTEnumeratorType) &&
                        implementedInterface.HasInstantiation)
                    {
                        // NOTE: we need to also do this for generic interfaces on arrays because they have a weird casting rule
                        // that doesn't require the implemented interface to be variant to consider it castable.
                        // For value types, we only need this when the array is castable by size (int[] and ICollection<uint>),
                        // or it's a reference type (Derived[] and ICollection<Base>).
                        TypeDesc elementType = _type.IsArray ? ((ArrayType)_type).ElementType : _type.Instantiation[0];
                        allInterfaceMethodsAreImplicitlyUsed =
                            CastingHelper.IsArrayElementTypeCastableBySize(elementType) ||
                            (elementType.IsDefType && !elementType.IsValueType);
                    }

                    if (allInterfaceMethodsAreImplicitlyUsed)
                    {
                        foreach (var interfaceMethod in implementedInterface.GetAllMethods())
                        {
                            if (interfaceMethod.Signature.IsStatic)
                                continue;

                            // Generic virtual methods are tracked by an orthogonal mechanism.
                            if (interfaceMethod.HasInstantiation)
                                continue;

                            MethodDesc implMethod = closestDefType.ResolveInterfaceMethodToVirtualMethodOnType(interfaceMethod);
                            if (implMethod != null)
                            {
                                dependencyList.Add(factory.VirtualMethodUse(interfaceMethod), "Variant interface method");
                                dependencyList.Add(factory.VirtualMethodUse(implMethod), "Variant interface method");
                            }
                        }
                    }
                }
            }
        }

        internal static bool MethodHasNonGenericILMethodBody(MethodDesc method)
        {
            // Generic methods have their own generic dictionaries
            if (method.HasInstantiation)
                return false;

            // Abstract methods don't have a body
            if (method.IsAbstract)
                return false;

            // PInvoke methods are not permitted on generic types,
            // but let's not crash the compilation because of that.
            if (method.IsPInvoke)
                return false;

            // CoreRT can generate method bodies for these no matter what (worst case
            // they'll be throwing). We don't want to take the "return false" code path on CoreRT because
            // delegate methods fall into the runtime implemented category on CoreRT, but we
            // just treat them like regular method bodies.
            return true;
        }

        protected override DependencyList ComputeNonRelocationBasedDependencies(NodeFactory factory)
        {
            DependencyList dependencies = new DependencyList();

            // Include the optional fields by default. We don't know if optional fields will be needed until
            // all of the interface usage has been stabilized. If we end up not needing it, the EEType node will not
            // generate any relocs to it, and the optional fields node will instruct the object writer to skip
            // emitting it.
            dependencies.Add(new DependencyListEntry(_optionalFieldsNode, "Optional fields"));

            // TODO-SIZE: We probably don't need to add these for all EETypes
            StaticsInfoHashtableNode.AddStaticsInfoDependencies(ref dependencies, factory, _type);

            if (EmitVirtualSlotsAndInterfaces)
            {
                if (!_type.IsArrayTypeWithoutGenericInterfaces())
                {
                    // Sealed vtables have relative pointers, so to minimize size, we build sealed vtables for the canonical types
                    dependencies.Add(new DependencyListEntry(factory.SealedVTable(_type.ConvertToCanonForm(CanonicalFormKind.Specific)), "Sealed Vtable"));
                }

                AddVirtualMethodUseDependencies(dependencies, factory);

                // Also add the un-normalized vtable slices of implemented interfaces.
                // This is important to do in the scanning phase so that the compilation phase can find
                // vtable information for things like IEnumerator<List<__Canon>>.
                foreach (TypeDesc intface in _type.RuntimeInterfaces)
                    dependencies.Add(factory.VTable(intface), "Interface vtable slice");
            }

            if (factory.CompilationModuleGroup.PresenceOfEETypeImpliesAllMethodsOnType(_type))
            {
                if (_type.IsArray || _type.IsDefType)
                {
                    // If the compilation group wants this type to be fully promoted, ensure that all non-generic methods of the 
                    // type are generated.
                    // This may be done for several reasons:
                    //   - The EEType may be going to be COMDAT folded with other EETypes generated in a different object file
                    //     This means their generic dictionaries need to have identical contents. The only way to achieve that is 
                    //     by generating the entries for all methods that contribute to the dictionary, and sorting the dictionaries.
                    //   - The generic type may be imported into another module, in which case the generic dictionary imported
                    //     must represent all of the methods, as the set of used methods cannot be known at compile time
                    //   - As a matter of policy, the type and its methods may be exported for use in another module. The policy
                    //     may wish to specify that if a type is to be placed into a shared module, all of the methods associated with
                    //     it should be also be exported.
                    foreach (var method in _type.GetClosestDefType().ConvertToCanonForm(CanonicalFormKind.Specific).GetAllMethods())
                    {
                        if (!MethodHasNonGenericILMethodBody(method))
                            continue;

                        dependencies.Add(factory.MethodEntrypoint(method.GetCanonMethodTarget(CanonicalFormKind.Specific)),
                            "Ensure all methods on type due to CompilationModuleGroup policy");
                    }
                }
            }

            if (!ConstructedEETypeNode.CreationAllowed(_type))
            {
                // If necessary EEType is the highest load level for this type, ask the metadata manager
                // if we have any dependencies due to reflectability.
                factory.MetadataManager.GetDependenciesDueToReflectability(ref dependencies, factory, _type);
            }

            return dependencies;
        }

        public override ObjectData GetData(NodeFactory factory, bool relocsOnly)
        {
            ObjectDataBuilder objData = new ObjectDataBuilder(factory, relocsOnly);
            objData.RequireInitialPointerAlignment();
            objData.AddSymbol(this);

            ComputeOptionalEETypeFields(factory, relocsOnly);

            OutputGCDesc(ref objData);
            OutputComponentSize(ref objData);
            OutputFlags(factory, ref objData);
            objData.EmitInt(BaseSize);
            OutputRelatedType(factory, ref objData);

            // Number of vtable slots will be only known later. Reseve the bytes for it.
            var vtableSlotCountReservation = objData.ReserveShort();

            // Number of interfaces will only be known later. Reserve the bytes for it.
            var interfaceCountReservation = objData.ReserveShort();

            objData.EmitInt(_type.GetHashCode());
            objData.EmitPointerReloc(factory.TypeManagerIndirection);

            if (EmitVirtualSlotsAndInterfaces)
            {
                // Emit VTable
                Debug.Assert(objData.CountBytes - ((ISymbolDefinitionNode)this).Offset == GetVTableOffset(objData.TargetPointerSize));
                SlotCounter virtualSlotCounter = SlotCounter.BeginCounting(ref /* readonly */ objData);
                OutputVirtualSlots(factory, ref objData, _type, _type, _type, relocsOnly);

                // Update slot count
                int numberOfVtableSlots = virtualSlotCounter.CountSlots(ref /* readonly */ objData);
                objData.EmitShort(vtableSlotCountReservation, checked((short)numberOfVtableSlots));

                // Emit interface map
                SlotCounter interfaceSlotCounter = SlotCounter.BeginCounting(ref /* readonly */ objData);
                OutputInterfaceMap(factory, ref objData);

                // Update slot count
                int numberOfInterfaceSlots = interfaceSlotCounter.CountSlots(ref /* readonly */ objData);
                objData.EmitShort(interfaceCountReservation, checked((short)numberOfInterfaceSlots));

            }
            else
            {
                // If we're not emitting any slots, the number of slots is zero.
                objData.EmitShort(vtableSlotCountReservation, 0);
                objData.EmitShort(interfaceCountReservation, 0);
            }

            OutputFinalizerMethod(factory, ref objData);
            OutputOptionalFields(factory, ref objData);
            OutputSealedVTable(factory, relocsOnly, ref objData);
            OutputGenericInstantiationDetails(factory, ref objData);

            return objData.ToObjectData();
        }

        /// <summary>
        /// Returns the offset within an EEType of the beginning of VTable entries
        /// </summary>
        /// <param name="pointerSize">The size of a pointer in bytes in the target architecture</param>
        public static int GetVTableOffset(int pointerSize)
        {
            return 16 + 2 * pointerSize;
        }

        protected virtual int GCDescSize => 0;

        protected virtual void OutputGCDesc(ref ObjectDataBuilder builder)
        {
            // Non-constructed EETypeNodes get no GC Desc
            Debug.Assert(GCDescSize == 0);
        }
        
        private void OutputComponentSize(ref ObjectDataBuilder objData)
        {
            if (_type.IsArray)
            {
                TypeDesc elementType = ((ArrayType)_type).ElementType;
                if (elementType == elementType.Context.UniversalCanonType)
                {
                    objData.EmitShort(0);
                }
                else
                {
                    int elementSize = elementType.GetElementSize().AsInt;
                    // We validated that this will fit the short when the node was constructed. No need for nice messages.
                    objData.EmitShort((short)checked((ushort)elementSize));
                }
            }
            else if (_type.IsString)
            {
                objData.EmitShort(StringComponentSize.Value);
            }
            else
            {
                objData.EmitShort(0);
            }
        }

        private void OutputFlags(NodeFactory factory, ref ObjectDataBuilder objData)
        {
            UInt16 flags = EETypeBuilderHelpers.ComputeFlags(_type);

            if (_type.GetTypeDefinition() == factory.ArrayOfTEnumeratorType)
            {
                // Generic array enumerators use special variance rules recognized by the runtime
                flags |= (UInt16)EETypeFlags.GenericVarianceFlag;
            }

            if (factory.TypeSystemContext.IsGenericArrayInterfaceType(_type))
            {
                // Runtime casting logic relies on all interface types implemented on arrays
                // to have the variant flag set (even if all the arguments are non-variant).
                // This supports e.g. casting uint[] to ICollection<int>
                flags |= (UInt16)EETypeFlags.GenericVarianceFlag;
            }

            if (!(this is CanonicalDefinitionEETypeNode))
            {
                foreach (DefType itf in _type.RuntimeInterfaces)
                {
                    if (itf == factory.ICastableInterface)
                    {
                        flags |= (UInt16)EETypeFlags.ICastableFlag;
                        break;
                    }
                }
            }               

            ISymbolNode relatedTypeNode = GetRelatedTypeNode(factory);

            // If the related type (base type / array element type / pointee type) is not part of this compilation group, and
            // the output binaries will be multi-file (not multiple object files linked together), indicate to the runtime
            // that it should indirect through the import address table
            if (relatedTypeNode != null && relatedTypeNode.RepresentsIndirectionCell)
            {
                flags |= (UInt16)EETypeFlags.RelatedTypeViaIATFlag;
            }

            if (HasOptionalFields)
            {
                flags |= (UInt16)EETypeFlags.OptionalFieldsFlag;
            }

            if (this is ClonedConstructedEETypeNode)
            {
                flags |= (UInt16)EETypeKind.ClonedEEType;
            }

            objData.EmitShort((short)flags);
        }

        protected virtual int BaseSize
        {
            get
            {
                int pointerSize = _type.Context.Target.PointerSize;
                int objectSize;

                if (_type.IsDefType)
                {
                    LayoutInt instanceByteCount = ((DefType)_type).InstanceByteCount;

                    if (instanceByteCount.IsIndeterminate)
                    {
                        // Some value must be put in, but the specific value doesn't matter as it
                        // isn't used for specific instantiations, and the universal canon eetype
                        // is never associated with an allocated object.
                        objectSize = pointerSize;
                    }
                    else
                    {
                        objectSize = pointerSize +
                            ((DefType)_type).InstanceByteCount.AsInt; // +pointerSize for SyncBlock
                    }

                    if (_type.IsValueType)
                        objectSize += pointerSize; // + EETypePtr field inherited from System.Object
                }
                else if (_type.IsArray)
                {
                    objectSize = 3 * pointerSize; // SyncBlock + EETypePtr + Length
                    if (_type.IsMdArray)
                        objectSize +=
                            2 * sizeof(int) * ((ArrayType)_type).Rank;
                }
                else if (_type.IsPointer)
                {
                    // These never get boxed and don't have a base size. Use a sentinel value recognized by the runtime.
                    return ParameterizedTypeShapeConstants.Pointer;
                }
                else if (_type.IsByRef)
                {
                    // These never get boxed and don't have a base size. Use a sentinel value recognized by the runtime.
                    return ParameterizedTypeShapeConstants.ByRef;
                }
                else
                    throw new NotImplementedException();

                objectSize = AlignmentHelper.AlignUp(objectSize, pointerSize);
                objectSize = Math.Max(MinimumObjectSize, objectSize);

                if (_type.IsString)
                {
                    // If this is a string, throw away objectSize we computed so far. Strings are special.
                    // SyncBlock + EETypePtr + length + firstChar
                    objectSize = 2 * pointerSize +
                        sizeof(int) +
                        StringComponentSize.Value;
                }

                return objectSize;
            }
        }

        protected virtual ISymbolNode GetBaseTypeNode(NodeFactory factory)
        {
            return _type.BaseType != null ? factory.NecessaryTypeSymbol(_type.BaseType) : null;
        }

        private ISymbolNode GetRelatedTypeNode(NodeFactory factory)
        {
            ISymbolNode relatedTypeNode = null;

            if (_type.IsArray || _type.IsPointer || _type.IsByRef)
            {
                var parameterType = ((ParameterizedType)_type).ParameterType;
                relatedTypeNode = factory.NecessaryTypeSymbol(parameterType);
            }
            else
            {
                TypeDesc baseType = _type.BaseType;
                if (baseType != null)
                {
                    relatedTypeNode = GetBaseTypeNode(factory);
                }
            }

            return relatedTypeNode;
        }

        protected virtual void OutputRelatedType(NodeFactory factory, ref ObjectDataBuilder objData)
        {
            ISymbolNode relatedTypeNode = GetRelatedTypeNode(factory);

            if (relatedTypeNode != null)
            {
                objData.EmitPointerReloc(relatedTypeNode);
            }
            else
            {
                objData.EmitZeroPointer();
            }
        }

        private void OutputVirtualSlots(NodeFactory factory, ref ObjectDataBuilder objData, TypeDesc implType, TypeDesc declType, TypeDesc templateType, bool relocsOnly)
        {
            Debug.Assert(EmitVirtualSlotsAndInterfaces);

            declType = declType.GetClosestDefType();
            templateType = templateType.ConvertToCanonForm(CanonicalFormKind.Specific);

            var baseType = declType.BaseType;
            if (baseType != null)
            {
                Debug.Assert(templateType.BaseType != null);
                OutputVirtualSlots(factory, ref objData, implType, baseType, templateType.BaseType, relocsOnly);
            }

            //
            // In the universal canonical types case, we could have base types in the hierarchy that are partial universal canonical types.
            // The presence of these types could cause incorrect vtable layouts, so we need to fully canonicalize them and walk the
            // hierarchy of the template type of the original input type to detect these cases.
            //
            // Exmaple: we begin with Derived<__UniversalCanon> and walk the template hierarchy:
            //
            //    class Derived<T> : Middle<T, MyStruct> { }    // -> Template is Derived<__UniversalCanon> and needs a dictionary slot
            //                                                  // -> Basetype tempalte is Middle<__UniversalCanon, MyStruct>. It's a partial
            //                                                        Universal canonical type, so we need to fully canonicalize it.
            //                                                  
            //    class Middle<T, U> : Base<U> { }              // -> Template is Middle<__UniversalCanon, __UniversalCanon> and needs a dictionary slot
            //                                                  // -> Basetype template is Base<__UniversalCanon>
            //
            //    class Base<T> { }                             // -> Template is Base<__UniversalCanon> and needs a dictionary slot.
            //
            // If we had not fully canonicalized the Middle class template, we would have ended up with Base<MyStruct>, which does not need
            // a dictionary slot, meaning we would have created a vtable layout that the runtime does not expect.
            //

            // The generic dictionary pointer occupies the first slot of each type vtable slice
            if (declType.HasGenericDictionarySlot() || templateType.HasGenericDictionarySlot())
            {
                // All generic interface types have a dictionary slot, but only some of them have an actual dictionary.
                bool isInterfaceWithAnEmptySlot = declType.IsInterface &&
                    declType.ConvertToCanonForm(CanonicalFormKind.Specific) == declType;

                // Note: Canonical type instantiations always have a generic dictionary vtable slot, but it's empty
                // Note: If the current EETypeNode represents a universal canonical type, any dictionary slot must be empty
                if (declType.IsCanonicalSubtype(CanonicalFormKind.Any)
                    || implType.IsCanonicalSubtype(CanonicalFormKind.Universal)
                    || factory.LazyGenericsPolicy.UsesLazyGenerics(declType)
                    || isInterfaceWithAnEmptySlot)
                    objData.EmitZeroPointer();
                else
                    objData.EmitPointerReloc(factory.TypeGenericDictionary(declType));
            }

            // It's only okay to touch the actual list of slots if we're in the final emission phase
            // or the vtable is not built lazily.
            if (relocsOnly && !factory.VTable(declType).HasFixedSlots)
                return;

            // Actual vtable slots follow
            IReadOnlyList<MethodDesc> virtualSlots = factory.VTable(declType).Slots;

            for (int i = 0; i < virtualSlots.Count; i++)
            {
                MethodDesc declMethod = virtualSlots[i];

                // No generic virtual methods can appear in the vtable!
                Debug.Assert(!declMethod.HasInstantiation);

                MethodDesc implMethod = implType.GetClosestDefType().FindVirtualFunctionTargetMethodOnObjectType(declMethod);

                // Final NewSlot methods cannot be overridden, and therefore can be placed in the sealed-vtable to reduce the size of the vtable
                // of this type and any type that inherits from it.
                if (declMethod.CanMethodBeInSealedVTable() && !declType.IsArrayTypeWithoutGenericInterfaces())
                    continue;

                if (!implMethod.IsAbstract)
                {
                    MethodDesc canonImplMethod = implMethod.GetCanonMethodTarget(CanonicalFormKind.Specific);
                    objData.EmitPointerReloc(factory.MethodEntrypoint(canonImplMethod, implMethod.OwningType.IsValueType));
                }
                else
                {
                    objData.EmitZeroPointer();
                }
            }
        }
        
        protected virtual IEETypeNode GetInterfaceTypeNode(NodeFactory factory, TypeDesc interfaceType)
        {
            return factory.NecessaryTypeSymbol(interfaceType);
        }

        protected virtual void OutputInterfaceMap(NodeFactory factory, ref ObjectDataBuilder objData)
        {
            Debug.Assert(EmitVirtualSlotsAndInterfaces);

            foreach (var itf in _type.RuntimeInterfaces)
            {
                objData.EmitPointerRelocOrIndirectionReference(GetInterfaceTypeNode(factory, itf));
            }
        }

        private void OutputFinalizerMethod(NodeFactory factory, ref ObjectDataBuilder objData)
        {
            if (_type.HasFinalizer)
            {
                MethodDesc finalizerMethod = _type.GetFinalizer();
                MethodDesc canonFinalizerMethod = finalizerMethod.GetCanonMethodTarget(CanonicalFormKind.Specific);
                objData.EmitPointerReloc(factory.MethodEntrypoint(canonFinalizerMethod));
            }
        }

        private void OutputOptionalFields(NodeFactory factory, ref ObjectDataBuilder objData)
        {
            if (HasOptionalFields)
            {
                objData.EmitPointerReloc(_optionalFieldsNode);
            }
        }

        private void OutputSealedVTable(NodeFactory factory, bool relocsOnly, ref ObjectDataBuilder objData)
        {
            if (EmitVirtualSlotsAndInterfaces && !_type.IsArrayTypeWithoutGenericInterfaces())
            {
                // Sealed vtables have relative pointers, so to minimize size, we build sealed vtables for the canonical types
                SealedVTableNode sealedVTable = factory.SealedVTable(_type.ConvertToCanonForm(CanonicalFormKind.Specific));

                if (sealedVTable.BuildSealedVTableSlots(factory, relocsOnly) && sealedVTable.NumSealedVTableEntries > 0)
                {
                    if (factory.Target.SupportsRelativePointers)
                        objData.EmitReloc(sealedVTable, RelocType.IMAGE_REL_BASED_RELPTR32);
                    else
                        objData.EmitPointerReloc(sealedVTable);
                }
            }
        }

        private void OutputGenericInstantiationDetails(NodeFactory factory, ref ObjectDataBuilder objData)
        {
            if (_type.HasInstantiation && !_type.IsTypeDefinition)
            {
                IEETypeNode typeDefNode = factory.NecessaryTypeSymbol(_type.GetTypeDefinition());
                if (factory.Target.SupportsRelativePointers)
                    objData.EmitRelativeRelocOrIndirectionReference(typeDefNode);
                else
                    objData.EmitPointerRelocOrIndirectionReference(typeDefNode);

                GenericCompositionDetails details;
                if (_type.GetTypeDefinition() == factory.ArrayOfTEnumeratorType)
                {
                    // Generic array enumerators use special variance rules recognized by the runtime
                    details = new GenericCompositionDetails(_type.Instantiation, new[] { GenericVariance.ArrayCovariant });
                }
                else if (factory.TypeSystemContext.IsGenericArrayInterfaceType(_type))
                {
                    // Runtime casting logic relies on all interface types implemented on arrays
                    // to have the variant flag set (even if all the arguments are non-variant).
                    // This supports e.g. casting uint[] to ICollection<int>
                    details = new GenericCompositionDetails(_type, forceVarianceInfo: true);
                }
                else
                    details = new GenericCompositionDetails(_type);

                ISymbolNode compositionNode = factory.GenericComposition(details);
                if (factory.Target.SupportsRelativePointers)
                    objData.EmitReloc(compositionNode, RelocType.IMAGE_REL_BASED_RELPTR32);
                else
                    objData.EmitPointerReloc(compositionNode);
            }
        }

        /// <summary>
        /// Populate the OptionalFieldsRuntimeBuilder if any optional fields are required.
        /// </summary>
        protected internal virtual void ComputeOptionalEETypeFields(NodeFactory factory, bool relocsOnly)
        {
            if (!relocsOnly && EmitVirtualSlotsAndInterfaces && InterfaceDispatchMapNode.MightHaveInterfaceDispatchMap(_type, factory))
            {
                _optionalFieldsBuilder.SetFieldValue(EETypeOptionalFieldTag.DispatchMap, checked((uint)factory.InterfaceDispatchMapIndirection(Type).IndexFromBeginningOfArray));
            }
            
            ComputeRareFlags(factory, relocsOnly);
            ComputeNullableValueOffset();
            if (!relocsOnly)
                ComputeICastableVirtualMethodSlots(factory);
            ComputeValueTypeFieldPadding();
        }

        void ComputeRareFlags(NodeFactory factory, bool relocsOnly)
        {
            uint flags = 0;

            MetadataType metadataType = _type as MetadataType;

            if (factory.TypeSystemContext.HasLazyStaticConstructor(_type))
            {
                flags |= (uint)EETypeRareFlags.HasCctorFlag;
            }

            if (_type.RequiresAlign8())
            {
                flags |= (uint)EETypeRareFlags.RequiresAlign8Flag;
            }

            TargetArchitecture targetArch = _type.Context.Target.Architecture;
            if (metadataType != null &&
                (targetArch == TargetArchitecture.ARM ||
                targetArch == TargetArchitecture.ARM64) &&
                metadataType.IsHfa)
            {
                flags |= (uint)EETypeRareFlags.IsHFAFlag;
            }

            if (metadataType != null && !_type.IsInterface && metadataType.IsAbstract)
            {
                flags |= (uint)EETypeRareFlags.IsAbstractClassFlag;
            }

            if (_type.IsByRefLike)
            {
                flags |= (uint)EETypeRareFlags.IsByRefLikeFlag;
            }

            if (EmitVirtualSlotsAndInterfaces && !_type.IsArrayTypeWithoutGenericInterfaces())
            {
                SealedVTableNode sealedVTable = factory.SealedVTable(_type.ConvertToCanonForm(CanonicalFormKind.Specific));
                if (sealedVTable.BuildSealedVTableSlots(factory, relocsOnly) && sealedVTable.NumSealedVTableEntries > 0)
                    flags |= (uint)EETypeRareFlags.HasSealedVTableEntriesFlag;
            }

            if (flags != 0)
            {
                _optionalFieldsBuilder.SetFieldValue(EETypeOptionalFieldTag.RareFlags, flags);
            }
        }

        /// <summary>
        /// To support boxing / unboxing, the offset of the value field of a Nullable type is recorded on the EEType.
        /// This is variable according to the alignment requirements of the Nullable&lt;T&gt; type parameter.
        /// </summary>
        void ComputeNullableValueOffset()
        {
            if (!_type.IsNullable)
                return;

            if (!_type.Instantiation[0].IsCanonicalSubtype(CanonicalFormKind.Universal))
            {
                var field = _type.GetKnownField("value");

                // In the definition of Nullable<T>, the first field should be the boolean representing "hasValue"
                Debug.Assert(field.Offset.AsInt > 0);

                // The contract with the runtime states the Nullable value offset is stored with the boolean "hasValue" size subtracted
                // to get a small encoding size win.
                _optionalFieldsBuilder.SetFieldValue(EETypeOptionalFieldTag.NullableValueOffset, (uint)field.Offset.AsInt - 1);
            }
        }

        /// <summary>
        /// ICastable is a special interface whose two methods are not invoked using regular interface dispatch.
        /// Instead, their VTable slots are recorded on the EEType of an object implementing ICastable and are
        /// called directly.
        /// </summary>
        protected virtual void ComputeICastableVirtualMethodSlots(NodeFactory factory)
        {
            if (_type.IsInterface || !EmitVirtualSlotsAndInterfaces)
                return;

            foreach (DefType itf in _type.RuntimeInterfaces)
            {
                if (itf == factory.ICastableInterface)
                {
                    MethodDesc isInstDecl = itf.GetKnownMethod("IsInstanceOfInterface", null);
                    MethodDesc getImplTypeDecl = itf.GetKnownMethod("GetImplType", null);

                    MethodDesc isInstMethodImpl = _type.ResolveInterfaceMethodTarget(isInstDecl);
                    MethodDesc getImplTypeMethodImpl = _type.ResolveInterfaceMethodTarget(getImplTypeDecl);

                    int isInstMethodSlot = VirtualMethodSlotHelper.GetVirtualMethodSlot(factory, isInstMethodImpl, _type);
                    int getImplTypeMethodSlot = VirtualMethodSlotHelper.GetVirtualMethodSlot(factory, getImplTypeMethodImpl, _type);

                    // Slots are usually -1, since these methods are usually in the sealed vtable of the base type.
                    if (isInstMethodSlot != -1)
                        _optionalFieldsBuilder.SetFieldValue(EETypeOptionalFieldTag.ICastableIsInstSlot, (uint)isInstMethodSlot);
                    if (getImplTypeMethodSlot != -1)
                        _optionalFieldsBuilder.SetFieldValue(EETypeOptionalFieldTag.ICastableGetImplTypeSlot, (uint)getImplTypeMethodSlot);
                }
            }
        }

        protected virtual void ComputeValueTypeFieldPadding()
        {
            // All objects that can have appreciable which can be derived from size compute ValueTypeFieldPadding. 
            // Unfortunately, the name ValueTypeFieldPadding is now wrong to avoid integration conflicts.

            // Interfaces, sealed types, and non-DefTypes cannot be derived from
            if (_type.IsInterface || !_type.IsDefType || (_type.IsSealed() && !_type.IsValueType))
                return;

            DefType defType = _type as DefType;
            Debug.Assert(defType != null);

            uint valueTypeFieldPaddingEncoded;

            if (defType.InstanceByteCount.IsIndeterminate)
            {
                valueTypeFieldPaddingEncoded = EETypeBuilderHelpers.ComputeValueTypeFieldPaddingFieldValue(0, 1, _type.Context.Target.PointerSize);
            }
            else
            {
                int numInstanceFieldBytes = defType.InstanceByteCountUnaligned.AsInt;

                // Check if we have a type derived from System.ValueType or System.Enum, but not System.Enum itself
                if (defType.IsValueType)
                {
                    // Value types should have at least 1 byte of size
                    Debug.Assert(numInstanceFieldBytes >= 1);

                    // The size doesn't currently include the EEType pointer size.  We need to add this so that 
                    // the number of instance field bytes consistently represents the boxed size.
                    numInstanceFieldBytes += _type.Context.Target.PointerSize;
                }

                // For unboxing to work correctly and for supporting dynamic type loading for derived types we need 
                // to record the actual size of the fields of a type without any padding for GC heap allocation (since 
                // we can unbox into locals or arrays where this padding is not used, and because field layout for derived
                // types is effected by the unaligned base size). We don't want to store this information for all EETypes 
                // since it's only relevant for value types, and derivable types so it's added as an optional field. It's 
                // also enough to simply store the size of the padding (between 0 and 4 or 8 bytes for 32-bit and 0 and 8 or 16 bytes 
                // for 64-bit) which cuts down our storage requirements.

                uint valueTypeFieldPadding = checked((uint)((BaseSize - _type.Context.Target.PointerSize) - numInstanceFieldBytes));
                valueTypeFieldPaddingEncoded = EETypeBuilderHelpers.ComputeValueTypeFieldPaddingFieldValue(valueTypeFieldPadding, (uint)defType.InstanceFieldAlignment.AsInt, _type.Context.Target.PointerSize);
            }

            if (valueTypeFieldPaddingEncoded != 0)
            {
                _optionalFieldsBuilder.SetFieldValue(EETypeOptionalFieldTag.ValueTypeFieldPadding, valueTypeFieldPaddingEncoded);
            }
        }

        protected override void OnMarked(NodeFactory context)
        {
            if (!context.IsCppCodegenTemporaryWorkaround)
            { 
                Debug.Assert(_type.IsTypeDefinition || !_type.HasSameTypeDefinition(context.ArrayOfTClass), "Asking for Array<T> EEType");
            }
        }

        public static void AddDependenciesForStaticsNode(NodeFactory factory, TypeDesc type, ref DependencyList dependencies)
        {
            // To ensure that the behvior of FieldInfo.GetValue/SetValue remains correct,
            // if a type may be reflectable, and it is generic, if a canonical instantiation of reflection
            // can exist which can refer to the associated type of this static base, ensure that type
            // has an EEType. (Which will allow the static field lookup logic to find the right type)
            if (type.HasInstantiation && !factory.MetadataManager.IsReflectionBlocked(type))
            {
                // TODO-SIZE: This current implementation is slightly generous, as it does not attempt to restrict
                // the created types to the maximum extent by investigating reflection data and such. Here we just
                // check if we support use of a canonically equivalent type to perform reflection.
                // We don't check to see if reflection is enabled on the type.
                if (factory.TypeSystemContext.SupportsUniversalCanon
                    || (factory.TypeSystemContext.SupportsCanon && (type != type.ConvertToCanonForm(CanonicalFormKind.Specific))))
                {
                    if (dependencies == null)
                        dependencies = new DependencyList();

                    dependencies.Add(factory.NecessaryTypeSymbol(type), "Static block owning type is necessary for canonically equivalent reflection");
                }
            }
        }

        protected static void AddDependenciesForUniversalGVMSupport(NodeFactory factory, TypeDesc type, ref DependencyList dependencies)
        {
            if (factory.TypeSystemContext.SupportsUniversalCanon)
            {
                foreach (MethodDesc method in type.GetMethods())
                {
                    if (!method.IsVirtual || !method.HasInstantiation)
                        continue;

                    if (method.IsAbstract)
                        continue;

                    TypeDesc[] universalCanonArray = new TypeDesc[method.Instantiation.Length];
                    for (int i = 0; i < universalCanonArray.Length; i++)
                        universalCanonArray[i] = factory.TypeSystemContext.UniversalCanonType;

                    MethodDesc universalCanonMethodNonCanonicalized = method.MakeInstantiatedMethod(new Instantiation(universalCanonArray));
                    MethodDesc universalCanonGVMMethod = universalCanonMethodNonCanonicalized.GetCanonMethodTarget(CanonicalFormKind.Universal);

                    if (dependencies == null)
                        dependencies = new DependencyList();

                    dependencies.Add(new DependencyListEntry(factory.MethodEntrypoint(universalCanonGVMMethod), "USG GVM Method"));
                }
            }
        }

        public override int ClassCode => 1521789141;

        public override int CompareToImpl(ISortableNode other, CompilerComparer comparer)
        {
            return comparer.Compare(_type, ((EETypeNode)other)._type);
        }

        private struct SlotCounter
        {
            private int _startBytes;

            public static SlotCounter BeginCounting(ref /* readonly */ ObjectDataBuilder builder)
                => new SlotCounter { _startBytes = builder.CountBytes };

            public int CountSlots(ref /* readonly */ ObjectDataBuilder builder)
            {
                int bytesEmitted = builder.CountBytes - _startBytes;
                Debug.Assert(bytesEmitted % builder.TargetPointerSize == 0);
                return bytesEmitted / builder.TargetPointerSize;
            }

        }
    }
}
