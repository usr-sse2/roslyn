﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Emit;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Symbols.Metadata.PE
{
    /// <summary>
    /// The class to represent all method parameters imported from a PE/module.
    /// </summary>
    internal class PEParameterSymbol : ParameterSymbol
    {
        [Flags]
        private enum WellKnownAttributeFlags
        {
            HasIDispatchConstantAttribute = 0x1 << 0,
            HasIUnknownConstantAttribute = 0x1 << 1,
            HasCallerFilePathAttribute = 0x1 << 2,
            HasCallerLineNumberAttribute = 0x1 << 3,
            HasCallerMemberNameAttribute = 0x1 << 4,
            IsCallerFilePath = 0x1 << 5,
            IsCallerLineNumber = 0x1 << 6,
            IsCallerMemberName = 0x1 << 7,
            NotNullWhenTrue = 0x1 << 8,
            NotNullWhenFalse = 0x1 << 9,
            AssertsTrue = 0x1 << 10,
            AssertsFalse = 0x1 << 11,
        }

        private struct PackedFlags
        {
            // Layout:
            // |.....|n|rr|cccccccccccc|vvvvvvvvvvvv|
            // 
            // v = decoded well known attribute values. 12 bits.
            // c = completion states for well known attributes. 1 if given attribute has been decoded, 0 otherwise. 12 bits.
            // r = RefKind. 2 bits.
            // n = hasNameInMetadata. 1 bit.

            private const int WellKnownAttributeDataOffset = 0;
            private const int WellKnownAttributeCompletionFlagOffset = 12;
            private const int RefKindOffset = 24;

            private const int RefKindMask = 0x3;
            private const int WellKnownAttributeDataMask = 0xFFF;
            private const int WellKnownAttributeCompletionFlagMask = WellKnownAttributeDataMask;

            private const int HasNameInMetadataBit = 0x1 << 26;

            private const int AllWellKnownAttributesCompleteNoData = WellKnownAttributeCompletionFlagMask << WellKnownAttributeCompletionFlagOffset;

            private int _bits;

            public RefKind RefKind
            {
                get { return (RefKind)((_bits >> RefKindOffset) & RefKindMask); }
            }

            public bool HasNameInMetadata
            {
                get { return (_bits & HasNameInMetadataBit) != 0; }
            }

#if DEBUG
            static PackedFlags()
            {
                // Verify a few things about the values we combine into flags.  This way, if they ever
                // change, this will get hit and you will know you have to update this type as well.

                // 1) Verify that the range of well known attributes doesn't fall outside the bounds of
                // the attribute completion and data mask.
                var attributeFlags = EnumUtilities.GetValues<WellKnownAttributeFlags>();
                var maxAttributeFlag = (int)System.Linq.Enumerable.Aggregate(attributeFlags, (f1, f2) => f1 | f2);
                Debug.Assert((maxAttributeFlag & WellKnownAttributeDataMask) == maxAttributeFlag);

                // 2) Verify that the range of ref kinds doesn't fall outside the bounds of
                // the ref kind mask.
                var refKinds = EnumUtilities.GetValues<RefKind>();
                var maxRefKind = (int)System.Linq.Enumerable.Aggregate(refKinds, (r1, r2) => r1 | r2);
                Debug.Assert((maxRefKind & RefKindMask) == maxRefKind);
            }
#endif

            public PackedFlags(RefKind refKind, bool attributesAreComplete, bool hasNameInMetadata)
            {
                int refKindBits = ((int)refKind & RefKindMask) << RefKindOffset;
                int attributeBits = attributesAreComplete ? AllWellKnownAttributesCompleteNoData : 0;
                int hasNameInMetadataBits = hasNameInMetadata ? HasNameInMetadataBit : 0;

                _bits = refKindBits | attributeBits | hasNameInMetadataBits;
            }

            public bool SetWellKnownAttribute(WellKnownAttributeFlags flag, bool value)
            {
                // a value has been decoded:
                int bitsToSet = (int)flag << WellKnownAttributeCompletionFlagOffset;
                if (value)
                {
                    // the actual value:
                    bitsToSet |= ((int)flag << WellKnownAttributeDataOffset);
                }

                ThreadSafeFlagOperations.Set(ref _bits, bitsToSet);
                return value;
            }

            public bool TryGetWellKnownAttribute(WellKnownAttributeFlags flag, out bool value)
            {
                int theBits = _bits; // Read this.bits once to ensure the consistency of the value and completion flags.
                value = (theBits & ((int)flag << WellKnownAttributeDataOffset)) != 0;
                return (theBits & ((int)flag << WellKnownAttributeCompletionFlagOffset)) != 0;
            }
        }

        private readonly Symbol _containingSymbol;
        private readonly string _name;
        private readonly TypeSymbolWithAnnotations _type;
        private readonly ParameterHandle _handle;
        private readonly ParameterAttributes _flags;
        private readonly PEModuleSymbol _moduleSymbol;

        private ImmutableArray<CSharpAttributeData> _lazyCustomAttributes;
        private ConstantValue _lazyDefaultValue = ConstantValue.Unset;
        private ThreeState _lazyIsParams;

        /// <summary>
        /// Attributes filtered out from m_lazyCustomAttributes, ParamArray, etc.
        /// </summary>
        private ImmutableArray<CSharpAttributeData> _lazyHiddenAttributes;

        private readonly ushort _ordinal;

        private PackedFlags _packedFlags;

        internal static PEParameterSymbol Create(
            PEModuleSymbol moduleSymbol,
            PEMethodSymbol containingSymbol,
            bool isContainingSymbolVirtual,
            int ordinal,
            ParamInfo<TypeSymbol> parameterInfo,
            ImmutableArray<byte> extraAnnotations,
            bool isReturn,
            out bool isBad)
        {
            return Create(
                moduleSymbol, containingSymbol, isContainingSymbolVirtual, ordinal,
                parameterInfo.IsByRef, parameterInfo.RefCustomModifiers, parameterInfo.Type, extraAnnotations,
                parameterInfo.Handle, parameterInfo.CustomModifiers, isReturn, out isBad);
        }

        /// <summary>
        /// Construct a parameter symbol for a property loaded from metadata.
        /// </summary>
        /// <param name="moduleSymbol"></param>
        /// <param name="containingSymbol"></param>
        /// <param name="ordinal"></param>
        /// <param name="handle">The property parameter doesn't have a name in metadata,
        /// so this is the handle of a corresponding accessor parameter, if there is one,
        /// or of the ParamInfo passed in, otherwise).</param>
        /// <param name="parameterInfo" />
        /// <param name="isBad" />
        internal static PEParameterSymbol Create(
            PEModuleSymbol moduleSymbol,
            PEPropertySymbol containingSymbol,
            bool isContainingSymbolVirtual,
            int ordinal,
            ParameterHandle handle,
            ParamInfo<TypeSymbol> parameterInfo,
            ImmutableArray<byte> extraAnnotations,
            out bool isBad)
        {
            return Create(
                moduleSymbol, containingSymbol, isContainingSymbolVirtual, ordinal,
                parameterInfo.IsByRef, parameterInfo.RefCustomModifiers, parameterInfo.Type, extraAnnotations,
                handle, parameterInfo.CustomModifiers, isReturn: false, out isBad);
        }

        private PEParameterSymbol(
            PEModuleSymbol moduleSymbol,
            Symbol containingSymbol,
            int ordinal,
            bool isByRef,
            TypeSymbolWithAnnotations type,
            ImmutableArray<byte> extraAnnotations,
            ParameterHandle handle,
            int countOfCustomModifiers,
            out bool isBad)
        {
            Debug.Assert((object)moduleSymbol != null);
            Debug.Assert((object)containingSymbol != null);
            Debug.Assert(ordinal >= 0);
            Debug.Assert(!type.IsNull);

            isBad = false;
            _moduleSymbol = moduleSymbol;
            _containingSymbol = containingSymbol;
            _ordinal = (ushort)ordinal;

            _handle = handle;

            RefKind refKind = RefKind.None;

            if (handle.IsNil)
            {
                refKind = isByRef ? RefKind.Ref : RefKind.None;

                type = TupleTypeSymbol.TryTransformToTuple(type.TypeSymbol, out TupleTypeSymbol tuple) ?
                    TypeSymbolWithAnnotations.Create(tuple) :
                    type;
                if (!extraAnnotations.IsDefault)
                {
                    type =  NullableTypeDecoder.TransformType(type, defaultTransformFlag: 0, extraAnnotations);
                }

                _lazyCustomAttributes = ImmutableArray<CSharpAttributeData>.Empty;
                _lazyHiddenAttributes = ImmutableArray<CSharpAttributeData>.Empty;
                _lazyDefaultValue = ConstantValue.NotAvailable;
                _lazyIsParams = ThreeState.False;
            }
            else
            {
                try
                {
                    moduleSymbol.Module.GetParamPropsOrThrow(handle, out _name, out _flags);
                }
                catch (BadImageFormatException)
                {
                    isBad = true;
                }

                if (isByRef)
                {
                    ParameterAttributes inOutFlags = _flags & (ParameterAttributes.Out | ParameterAttributes.In);

                    if (inOutFlags == ParameterAttributes.Out)
                    {
                        refKind = RefKind.Out;
                    }
                    else if (moduleSymbol.Module.HasIsReadOnlyAttribute(handle))
                    {
                        refKind = RefKind.In;
                    }
                    else
                    {
                        refKind = RefKind.Ref;
                    }
                }

                // CONSIDER: Can we make parameter type computation lazy?
                var typeSymbol = DynamicTypeDecoder.TransformType(type.TypeSymbol, countOfCustomModifiers, handle, moduleSymbol, refKind);
                type = type.WithTypeAndModifiers(typeSymbol, type.CustomModifiers);
                // Decode nullable before tuple types to avoid converting between
                // NamedTypeSymbol and TupleTypeSymbol unnecessarily.
                type = NullableTypeDecoder.TransformType(type, handle, moduleSymbol, extraAnnotations);
                type = TupleTypeDecoder.DecodeTupleTypesIfApplicable(type, handle, moduleSymbol);
            }

            _type = type;

            bool hasNameInMetadata = !string.IsNullOrEmpty(_name);
            if (!hasNameInMetadata)
            {
                // As was done historically, if the parameter doesn't have a name, we give it the name "value".
                _name = "value";
            }

            _packedFlags = new PackedFlags(refKind, attributesAreComplete: handle.IsNil, hasNameInMetadata: hasNameInMetadata);

            Debug.Assert(refKind == this.RefKind);
            Debug.Assert(hasNameInMetadata == this.HasNameInMetadata);
        }

        private bool HasNameInMetadata
        {
            get
            {
                return _packedFlags.HasNameInMetadata;
            }
        }

        private static PEParameterSymbol Create(
            PEModuleSymbol moduleSymbol,
            Symbol containingSymbol,
            bool isContainingSymbolVirtual,
            int ordinal,
            bool isByRef,
            ImmutableArray<ModifierInfo<TypeSymbol>> refCustomModifiers,
            TypeSymbol type,
            ImmutableArray<byte> extraAnnotations,
            ParameterHandle handle,
            ImmutableArray<ModifierInfo<TypeSymbol>> customModifiers,
            bool isReturn,
            out bool isBad)
        {
            // We start without annotation (they will be decoded below)
            var typeWithModifiers = TypeSymbolWithAnnotations.Create(type, customModifiers: CSharpCustomModifier.Convert(customModifiers));

            PEParameterSymbol parameter = customModifiers.IsDefaultOrEmpty && refCustomModifiers.IsDefaultOrEmpty
                ? new PEParameterSymbol(moduleSymbol, containingSymbol, ordinal, isByRef, typeWithModifiers, extraAnnotations, handle, 0, out isBad)
                : new PEParameterSymbolWithCustomModifiers(moduleSymbol, containingSymbol, ordinal, isByRef, refCustomModifiers, typeWithModifiers, extraAnnotations, handle, out isBad);

            bool hasInAttributeModifier = parameter.RefCustomModifiers.HasInAttributeModifier();

            if (isReturn)
            {
                // A RefReadOnly return parameter should always have this modreq, and vice versa.
                isBad |= (parameter.RefKind == RefKind.RefReadOnly) != hasInAttributeModifier;
            }
            else if (parameter.RefKind == RefKind.In)
            {
                // An in parameter should not have this modreq, unless the containing symbol was virtual or abstract.
                isBad |= isContainingSymbolVirtual != hasInAttributeModifier;
            }
            else if (hasInAttributeModifier)
            {
                // This modreq should not exist on non-in parameters.
                isBad = true;
            }

            return parameter;
        }

        private sealed class PEParameterSymbolWithCustomModifiers : PEParameterSymbol
        {
            private readonly ImmutableArray<CustomModifier> _refCustomModifiers;

            public PEParameterSymbolWithCustomModifiers(
                PEModuleSymbol moduleSymbol,
                Symbol containingSymbol,
                int ordinal,
                bool isByRef,
                ImmutableArray<ModifierInfo<TypeSymbol>> refCustomModifiers,
                TypeSymbolWithAnnotations type,
                ImmutableArray<byte> extraAnnotations,
                ParameterHandle handle,
                out bool isBad) :
                    base(moduleSymbol, containingSymbol, ordinal, isByRef, type, extraAnnotations, handle,
                         refCustomModifiers.NullToEmpty().Length + type.CustomModifiers.Length,
                         out isBad)
            {
                _refCustomModifiers = CSharpCustomModifier.Convert(refCustomModifiers);

                Debug.Assert(_refCustomModifiers.IsEmpty || isByRef);
            }

            public override ImmutableArray<CustomModifier> RefCustomModifiers
            {
                get
                {
                    return _refCustomModifiers;
                }
            }
        }

        public override RefKind RefKind
        {
            get
            {
                return _packedFlags.RefKind;
            }
        }

        public override string Name
        {
            get
            {
                return _name;
            }
        }

        public override string MetadataName
        {
            get
            {
                return HasNameInMetadata ? _name : string.Empty;
            }
        }

        internal ParameterAttributes Flags
        {
            get
            {
                return _flags;
            }
        }

        public override int Ordinal
        {
            get
            {
                return _ordinal;
            }
        }

        // might be Nil
        internal ParameterHandle Handle
        {
            get
            {
                return _handle;
            }
        }

        public override Symbol ContainingSymbol
        {
            get
            {
                return _containingSymbol;
            }
        }

        internal override bool HasMetadataConstantValue
        {
            get
            {
                return (_flags & ParameterAttributes.HasDefault) != 0;
            }
        }

        /// <remarks>
        /// Internal for testing.  Non-test code should use <see cref="ExplicitDefaultConstantValue"/>.
        /// </remarks>
        internal ConstantValue ImportConstantValue(bool ignoreAttributes = false)
        {
            Debug.Assert(!_handle.IsNil);

            // Metadata Spec 22.33: 
            //   6. If Flags.HasDefault = 1 then this row [of Param table] shall own exactly one row in the Constant table [ERROR]
            //   7. If Flags.HasDefault = 0, then there shall be no rows in the Constant table owned by this row [ERROR]
            ConstantValue value = null;

            if ((_flags & ParameterAttributes.HasDefault) != 0)
            {
                value = _moduleSymbol.Module.GetParamDefaultValue(_handle);
            }

            if (value == null && !ignoreAttributes)
            {
                value = GetDefaultDecimalOrDateTimeValue();
            }

            return value;
        }

        internal override ConstantValue ExplicitDefaultConstantValue
        {
            get
            {
                // The HasDefault flag has to be set, it doesn't suffice to mark the parameter with DefaultParameterValueAttribute.
                if (_lazyDefaultValue == ConstantValue.Unset)
                {
                    // From the C# point of view, there is no need to import a parameter's default value
                    // if the language isn't going to treat it as optional. However, we might need metadata constant value for NoPia.
                    // NOTE: Ignoring attributes for non-Optional parameters disrupts round-tripping, but the trade-off seems acceptable.
                    ConstantValue value = ImportConstantValue(ignoreAttributes: !IsMetadataOptional);
                    Interlocked.CompareExchange(ref _lazyDefaultValue, value, ConstantValue.Unset);
                }

                return _lazyDefaultValue;
            }
        }

        private ConstantValue GetDefaultDecimalOrDateTimeValue()
        {
            Debug.Assert(!_handle.IsNil);
            ConstantValue value = null;

            // It is possible in Visual Basic for a parameter of object type to have a default value of DateTime type.
            // If it's present, use it.  We'll let the call-site figure out whether it can actually be used.
            if (_moduleSymbol.Module.HasDateTimeConstantAttribute(_handle, out value))
            {
                return value;
            }

            // It is possible in Visual Basic for a parameter of object type to have a default value of decimal type.
            // If it's present, use it.  We'll let the call-site figure out whether it can actually be used.
            if (_moduleSymbol.Module.HasDecimalConstantAttribute(_handle, out value))
            {
                return value;
            }

            return value;
        }

        internal override bool IsMetadataOptional
        {
            get
            {
                return (_flags & ParameterAttributes.Optional) != 0;
            }
        }

        internal override bool IsIDispatchConstant
        {
            get
            {
                const WellKnownAttributeFlags flag = WellKnownAttributeFlags.HasIDispatchConstantAttribute;

                bool value;
                if (!_packedFlags.TryGetWellKnownAttribute(flag, out value))
                {
                    value = _packedFlags.SetWellKnownAttribute(flag, _moduleSymbol.Module.HasAttribute(_handle,
                        AttributeDescription.IDispatchConstantAttribute));
                }
                return value;
            }
        }

        internal override bool IsIUnknownConstant
        {
            get
            {
                const WellKnownAttributeFlags flag = WellKnownAttributeFlags.HasIUnknownConstantAttribute;

                bool value;
                if (!_packedFlags.TryGetWellKnownAttribute(flag, out value))
                {
                    value = _packedFlags.SetWellKnownAttribute(flag, _moduleSymbol.Module.HasAttribute(_handle,
                        AttributeDescription.IUnknownConstantAttribute));
                }
                return value;
            }
        }

        private bool HasCallerLineNumberAttribute
        {
            get
            {
                const WellKnownAttributeFlags flag = WellKnownAttributeFlags.HasCallerLineNumberAttribute;

                bool value;
                if (!_packedFlags.TryGetWellKnownAttribute(flag, out value))
                {
                    value = _packedFlags.SetWellKnownAttribute(flag, _moduleSymbol.Module.HasAttribute(_handle,
                        AttributeDescription.CallerLineNumberAttribute));
                }
                return value;
            }
        }

        private bool HasCallerFilePathAttribute
        {
            get
            {
                const WellKnownAttributeFlags flag = WellKnownAttributeFlags.HasCallerFilePathAttribute;

                bool value;
                if (!_packedFlags.TryGetWellKnownAttribute(flag, out value))
                {
                    value = _packedFlags.SetWellKnownAttribute(flag, _moduleSymbol.Module.HasAttribute(_handle,
                        AttributeDescription.CallerFilePathAttribute));
                }
                return value;
            }
        }

        private bool HasCallerMemberNameAttribute
        {
            get
            {
                const WellKnownAttributeFlags flag = WellKnownAttributeFlags.HasCallerMemberNameAttribute;

                bool value;
                if (!_packedFlags.TryGetWellKnownAttribute(flag, out value))
                {
                    value = _packedFlags.SetWellKnownAttribute(flag, _moduleSymbol.Module.HasAttribute(_handle,
                        AttributeDescription.CallerMemberNameAttribute));
                }
                return value;
            }
        }

        internal override bool IsCallerLineNumber
        {
            get
            {
                const WellKnownAttributeFlags flag = WellKnownAttributeFlags.IsCallerLineNumber;

                bool value;
                if (!_packedFlags.TryGetWellKnownAttribute(flag, out value))
                {
                    HashSet<DiagnosticInfo> useSiteDiagnostics = null;
                    bool isCallerLineNumber = HasCallerLineNumberAttribute
                        && new TypeConversions(ContainingAssembly).HasCallerLineNumberConversion(this.Type.TypeSymbol, ref useSiteDiagnostics);

                    value = _packedFlags.SetWellKnownAttribute(flag, isCallerLineNumber);
                }
                return value;
            }
        }

        internal override bool IsCallerFilePath
        {
            get
            {
                const WellKnownAttributeFlags flag = WellKnownAttributeFlags.IsCallerFilePath;

                bool value;
                if (!_packedFlags.TryGetWellKnownAttribute(flag, out value))
                {
                    HashSet<DiagnosticInfo> useSiteDiagnostics = null;
                    bool isCallerFilePath = !HasCallerLineNumberAttribute
                        && HasCallerFilePathAttribute
                        && new TypeConversions(ContainingAssembly).HasCallerInfoStringConversion(this.Type.TypeSymbol, ref useSiteDiagnostics);

                    value = _packedFlags.SetWellKnownAttribute(flag, isCallerFilePath);
                }
                return value;
            }
        }

        internal override bool IsCallerMemberName
        {
            get
            {
                const WellKnownAttributeFlags flag = WellKnownAttributeFlags.IsCallerMemberName;

                bool value;
                if (!_packedFlags.TryGetWellKnownAttribute(flag, out value))
                {
                    HashSet<DiagnosticInfo> useSiteDiagnostics = null;
                    bool isCallerMemberName = !HasCallerLineNumberAttribute
                        && !HasCallerFilePathAttribute
                        && HasCallerMemberNameAttribute
                        && new TypeConversions(ContainingAssembly).HasCallerInfoStringConversion(this.Type.TypeSymbol, ref useSiteDiagnostics);

                    value = _packedFlags.SetWellKnownAttribute(flag, isCallerMemberName);
                }
                return value;
            }
        }

        internal override FlowAnalysisAnnotations FlowAnalysisAnnotations
        {
            get
            {
                const WellKnownAttributeFlags notNullWhenTrue = WellKnownAttributeFlags.NotNullWhenTrue;
                const WellKnownAttributeFlags notNullWhenFalse = WellKnownAttributeFlags.NotNullWhenFalse;
                const WellKnownAttributeFlags assertsTrue = WellKnownAttributeFlags.AssertsTrue;
                const WellKnownAttributeFlags assertsFalse = WellKnownAttributeFlags.AssertsFalse;

                if (!_packedFlags.TryGetWellKnownAttribute(notNullWhenTrue, out bool hasNotNullWhenTrue) ||
                    !_packedFlags.TryGetWellKnownAttribute(notNullWhenFalse, out bool hasNotNullWhenFalse) ||
                    !_packedFlags.TryGetWellKnownAttribute(assertsTrue, out bool hasAssertsTrue) ||
                    !_packedFlags.TryGetWellKnownAttribute(assertsFalse, out bool hasAssertsFalse))
                {
                    FlowAnalysisAnnotations? annotations = TryGetExtraAttributeAnnotations();

                    if (annotations.HasValue)
                    {
                        // External annotations win, if any is present on the member
                        hasNotNullWhenTrue = (annotations & FlowAnalysisAnnotations.NotNullWhenTrue) != 0;
                        hasNotNullWhenFalse = (annotations & FlowAnalysisAnnotations.NotNullWhenFalse) != 0;
                        hasAssertsTrue = (annotations & FlowAnalysisAnnotations.AssertsTrue) != 0;
                        hasAssertsFalse = (annotations & FlowAnalysisAnnotations.AssertsFalse) != 0;
                    }
                    else
                    {
                        bool hasEnsuresNotNull = _moduleSymbol.Module.HasAttribute(_handle, AttributeDescription.EnsuresNotNullAttribute);
                        hasNotNullWhenTrue = hasEnsuresNotNull || _moduleSymbol.Module.HasAttribute(_handle, AttributeDescription.NotNullWhenTrueAttribute);
                        hasNotNullWhenFalse = hasEnsuresNotNull || _moduleSymbol.Module.HasAttribute(_handle, AttributeDescription.NotNullWhenFalseAttribute);
                        hasAssertsTrue = _moduleSymbol.Module.HasAttribute(_handle, AttributeDescription.AssertsTrueAttribute);
                        hasAssertsFalse = _moduleSymbol.Module.HasAttribute(_handle, AttributeDescription.AssertsFalseAttribute);
                    }

                    _packedFlags.SetWellKnownAttribute(notNullWhenTrue, hasNotNullWhenTrue);
                    _packedFlags.SetWellKnownAttribute(notNullWhenFalse, hasNotNullWhenFalse);
                    _packedFlags.SetWellKnownAttribute(assertsTrue, hasAssertsTrue);
                    _packedFlags.SetWellKnownAttribute(assertsFalse, hasAssertsFalse);

                    if (annotations.HasValue)
                    {
                        return annotations.Value;
                    }
                }

                return FlowAnalysisAnnotationsFacts.Create(
                    notNullWhenTrue: hasNotNullWhenTrue, notNullWhenFalse: hasNotNullWhenFalse,
                    assertsTrue: hasAssertsTrue, assertsFalse: hasAssertsFalse);
            }
        }

        public override TypeSymbolWithAnnotations Type
        {
            get
            {
                return _type;
            }
        }

        public override ImmutableArray<CustomModifier> RefCustomModifiers
        {
            get
            {
                return ImmutableArray<CustomModifier>.Empty;
            }
        }

        internal override bool IsMetadataIn
        {
            get { return (_flags & ParameterAttributes.In) != 0; }
        }

        internal override bool IsMetadataOut
        {
            get { return (_flags & ParameterAttributes.Out) != 0; }
        }

        internal override bool IsMarshalledExplicitly
        {
            get
            {
                return (_flags & ParameterAttributes.HasFieldMarshal) != 0;
            }
        }

        internal override MarshalPseudoCustomAttributeData MarshallingInformation
        {
            get
            {
                // the compiler doesn't need full marshalling information, just the unmanaged type or descriptor
                return null;
            }
        }

        internal override ImmutableArray<byte> MarshallingDescriptor
        {
            get
            {
                if ((_flags & ParameterAttributes.HasFieldMarshal) == 0)
                {
                    return default(ImmutableArray<byte>);
                }

                Debug.Assert(!_handle.IsNil);
                return _moduleSymbol.Module.GetMarshallingDescriptor(_handle);
            }
        }

        internal override UnmanagedType MarshallingType
        {
            get
            {
                if ((_flags & ParameterAttributes.HasFieldMarshal) == 0)
                {
                    return 0;
                }

                Debug.Assert(!_handle.IsNil);
                return _moduleSymbol.Module.GetMarshallingType(_handle);
            }
        }

        public override bool IsParams
        {
            get
            {
                // This is also populated by loading attributes, but loading
                // attributes is more expensive, so we should only do it if
                // attributes are requested.
                if (!_lazyIsParams.HasValue())
                {
                    _lazyIsParams = _moduleSymbol.Module.HasParamsAttribute(_handle).ToThreeState();
                }
                return _lazyIsParams.Value();
            }
        }

        public override ImmutableArray<Location> Locations
        {
            get
            {
                return _containingSymbol.Locations;
            }
        }

        public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences
        {
            get
            {
                return ImmutableArray<SyntaxReference>.Empty;
            }
        }

        public override ImmutableArray<CSharpAttributeData> GetAttributes()
        {
            if (_lazyCustomAttributes.IsDefault)
            {
                Debug.Assert(!_handle.IsNil);
                var containingPEModuleSymbol = (PEModuleSymbol)this.ContainingModule;

                // Filter out ParamArrayAttributes if necessary and cache
                // the attribute handle for GetCustomAttributesToEmit
                bool filterOutParamArrayAttribute = (!_lazyIsParams.HasValue() || _lazyIsParams.Value());

                ConstantValue defaultValue = this.ExplicitDefaultConstantValue;
                AttributeDescription filterOutConstantAttributeDescription = default(AttributeDescription);

                if ((object)defaultValue != null)
                {
                    if (defaultValue.Discriminator == ConstantValueTypeDiscriminator.DateTime)
                    {
                        filterOutConstantAttributeDescription = AttributeDescription.DateTimeConstantAttribute;
                    }
                    else if (defaultValue.Discriminator == ConstantValueTypeDiscriminator.Decimal)
                    {
                        filterOutConstantAttributeDescription = AttributeDescription.DecimalConstantAttribute;
                    }
                }

                bool filterIsReadOnlyAttribute = this.RefKind == RefKind.In;

                if (filterOutParamArrayAttribute || filterOutConstantAttributeDescription.Signatures != null || filterIsReadOnlyAttribute)
                {
                    CustomAttributeHandle paramArrayAttribute;
                    CustomAttributeHandle constantAttribute;
                    CustomAttributeHandle isReadOnlyAttribute;

                    ImmutableArray<CSharpAttributeData> attributes =
                        containingPEModuleSymbol.GetCustomAttributesForToken(
                            _handle,
                            out paramArrayAttribute,
                            filterOutParamArrayAttribute ? AttributeDescription.ParamArrayAttribute : default,
                            out constantAttribute,
                            filterOutConstantAttributeDescription,
                            out isReadOnlyAttribute,
                            filterIsReadOnlyAttribute ? AttributeDescription.IsReadOnlyAttribute : default,
                            out _,
                            default);

                    if (!paramArrayAttribute.IsNil || !constantAttribute.IsNil)
                    {
                        var builder = ArrayBuilder<CSharpAttributeData>.GetInstance();

                        if (!paramArrayAttribute.IsNil)
                        {
                            builder.Add(new PEAttributeData(containingPEModuleSymbol, paramArrayAttribute));
                        }

                        if (!constantAttribute.IsNil)
                        {
                            builder.Add(new PEAttributeData(containingPEModuleSymbol, constantAttribute));
                        }

                        ImmutableInterlocked.InterlockedInitialize(ref _lazyHiddenAttributes, builder.ToImmutableAndFree());
                    }
                    else
                    {
                        ImmutableInterlocked.InterlockedInitialize(ref _lazyHiddenAttributes, ImmutableArray<CSharpAttributeData>.Empty);
                    }

                    if (!_lazyIsParams.HasValue())
                    {
                        Debug.Assert(filterOutParamArrayAttribute);
                        _lazyIsParams = (!paramArrayAttribute.IsNil).ToThreeState();
                    }

                    ImmutableInterlocked.InterlockedInitialize(
                        ref _lazyCustomAttributes,
                        attributes);
                }
                else
                {
                    ImmutableInterlocked.InterlockedInitialize(ref _lazyHiddenAttributes, ImmutableArray<CSharpAttributeData>.Empty);
                    containingPEModuleSymbol.LoadCustomAttributes(_handle, ref _lazyCustomAttributes);
                }
            }

            Debug.Assert(!_lazyHiddenAttributes.IsDefault);
            return _lazyCustomAttributes;
        }

        internal override IEnumerable<CSharpAttributeData> GetCustomAttributesToEmit(PEModuleBuilder moduleBuilder)
        {
            foreach (CSharpAttributeData attribute in GetAttributes())
            {
                yield return attribute;
            }

            // Yield hidden attributes last, order might be important.
            foreach (CSharpAttributeData attribute in _lazyHiddenAttributes)
            {
                yield return attribute;
            }
        }

        internal sealed override CSharpCompilation DeclaringCompilation // perf, not correctness
        {
            get { return null; }
        }
    }
}
