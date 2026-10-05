// Yalnızca netstandard2.0 / net462 derlemelerine eklenir (Directory.Build.targets). .NET 8+ bu türleri BCL'de içerir.
// Modern C# özelliklerinin (init, required, record, CallerArgumentExpression, null analizi, trim/AOT öznitelikleri)
// eski hedeflerde derlenebilmesi için derleyicinin beklediği tür tanımlarıdır; davranış içermezler.
#pragma warning disable SA1402, SA1649, CA1019, CA1813, CA1018

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit;

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute;

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute(string featureName) : Attribute
    {
        public string FeatureName { get; } = featureName;

        public bool IsOptional { get; init; }
    }

    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class CallerArgumentExpressionAttribute(string parameterName) : Attribute
    {
        public string ParameterName { get; } = parameterName;
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute;

    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class NotNullWhenAttribute(bool returnValue) : Attribute
    {
        public bool ReturnValue { get; } = returnValue;
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class MaybeNullWhenAttribute(bool returnValue) : Attribute
    {
        public bool ReturnValue { get; } = returnValue;
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue)]
    internal sealed class NotNullAttribute : Attribute;

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue)]
    internal sealed class MaybeNullAttribute : Attribute;

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property)]
    internal sealed class AllowNullAttribute : Attribute;

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property)]
    internal sealed class DisallowNullAttribute : Attribute;

    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class DoesNotReturnAttribute : Attribute;

    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class DoesNotReturnIfAttribute(bool parameterValue) : Attribute
    {
        public bool ParameterValue { get; } = parameterValue;
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = false, AllowMultiple = true)]
    internal sealed class MemberNotNullAttribute(params string[] members) : Attribute
    {
        public string[] Members { get; } = members;
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Class, Inherited = false)]
    internal sealed class RequiresDynamicCodeAttribute(string message) : Attribute
    {
        public string Message { get; } = message;

        public string? Url { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Class, Inherited = false)]
    internal sealed class RequiresUnreferencedCodeAttribute(string message) : Attribute
    {
        public string Message { get; } = message;

        public string? Url { get; set; }
    }

    [AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)]
    internal sealed class UnconditionalSuppressMessageAttribute(string category, string checkId) : Attribute
    {
        public string Category { get; } = category;

        public string CheckId { get; } = checkId;

        public string? Justification { get; set; }
    }

    [Flags]
    internal enum DynamicallyAccessedMemberTypes
    {
        None = 0,
        PublicParameterlessConstructor = 0x0001,
        PublicConstructors = 0x0002 | PublicParameterlessConstructor,
        All = ~None
    }

    [AttributeUsage(
        AttributeTargets.Field | AttributeTargets.ReturnValue | AttributeTargets.GenericParameter | AttributeTargets.Parameter |
        AttributeTargets.Property | AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct,
        Inherited = false)]
    internal sealed class DynamicallyAccessedMembersAttribute(DynamicallyAccessedMemberTypes memberTypes) : Attribute
    {
        public DynamicallyAccessedMemberTypes MemberTypes { get; } = memberTypes;
    }
}
