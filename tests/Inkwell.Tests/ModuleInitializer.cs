#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    /// <summary>.NET Framework 4.8 doesn't ship this attribute; the compiler only needs it to exist.</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute
    {
    }
}
#endif
