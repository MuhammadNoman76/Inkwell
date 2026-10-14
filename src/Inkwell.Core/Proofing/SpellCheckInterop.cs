using System.Runtime.InteropServices;

namespace Inkwell.Core.Proofing;

/// <summary>
/// The Windows Spell Checking API (spellcheck.h, Windows 8 and later), declared by hand: the interfaces in vtable
/// order with their IIDs. Every method returns its HRESULT ([PreserveSig]) so failures never become exceptions
/// inside COM calls. Strings returned by the API (LPWSTR) are read and freed with CoTaskMemFree by the caller.
/// </summary>
internal static class SpellCheckInterop
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;

    /// <summary>CLSID_SpellCheckerFactory.</summary>
    public static readonly Guid FactoryClsid = new("7AB36653-1796-484B-BDFA-E74F1DB7C1DC");

    [ComImport]
    [Guid("8E018A9D-2415-4677-BF08-794EA61F94BB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISpellCheckerFactory
    {
        [PreserveSig] int get_SupportedLanguages(out IEnumString? value);

        [PreserveSig] int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out int value);

        [PreserveSig] int CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out ISpellChecker? value);
    }

    [ComImport]
    [Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISpellChecker
    {
        [PreserveSig] int get_LanguageTag(out IntPtr value);

        [PreserveSig] int Check([MarshalAs(UnmanagedType.LPWStr)] string text, out IEnumSpellingError? value);

        [PreserveSig] int Suggest([MarshalAs(UnmanagedType.LPWStr)] string word, out IEnumString? value);

        [PreserveSig] int Add([MarshalAs(UnmanagedType.LPWStr)] string word);

        [PreserveSig] int Ignore([MarshalAs(UnmanagedType.LPWStr)] string word);

        [PreserveSig] int AutoCorrect([MarshalAs(UnmanagedType.LPWStr)] string from, [MarshalAs(UnmanagedType.LPWStr)] string to);

        [PreserveSig] int GetOptionValue([MarshalAs(UnmanagedType.LPWStr)] string optionId, out byte value);

        [PreserveSig] int get_OptionIds(out IEnumString? value);

        [PreserveSig] int get_Id(out IntPtr value);

        [PreserveSig] int get_LocalizedName(out IntPtr value);

        [PreserveSig] int add_SpellCheckerChanged(IntPtr handler, out uint eventCookie);

        [PreserveSig] int remove_SpellCheckerChanged(uint eventCookie);

        [PreserveSig] int GetOptionDescription([MarshalAs(UnmanagedType.LPWStr)] string optionId, out IntPtr value);

        [PreserveSig] int ComprehensiveCheck([MarshalAs(UnmanagedType.LPWStr)] string text, out IEnumSpellingError? value);
    }

    /// <summary>ISpellChecker2 (Windows 10): ISpellChecker's methods, then Remove. Declared flat because .NET COM interop
    /// doesn't lay out inherited interface methods in the derived vtable.</summary>
    [ComImport]
    [Guid("E7ED1C71-87F7-4378-A840-C9200DACEE47")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISpellChecker2
    {
        [PreserveSig] int get_LanguageTag(out IntPtr value);

        [PreserveSig] int Check([MarshalAs(UnmanagedType.LPWStr)] string text, out IEnumSpellingError? value);

        [PreserveSig] int Suggest([MarshalAs(UnmanagedType.LPWStr)] string word, out IEnumString? value);

        [PreserveSig] int Add([MarshalAs(UnmanagedType.LPWStr)] string word);

        [PreserveSig] int Ignore([MarshalAs(UnmanagedType.LPWStr)] string word);

        [PreserveSig] int AutoCorrect([MarshalAs(UnmanagedType.LPWStr)] string from, [MarshalAs(UnmanagedType.LPWStr)] string to);

        [PreserveSig] int GetOptionValue([MarshalAs(UnmanagedType.LPWStr)] string optionId, out byte value);

        [PreserveSig] int get_OptionIds(out IEnumString? value);

        [PreserveSig] int get_Id(out IntPtr value);

        [PreserveSig] int get_LocalizedName(out IntPtr value);

        [PreserveSig] int add_SpellCheckerChanged(IntPtr handler, out uint eventCookie);

        [PreserveSig] int remove_SpellCheckerChanged(uint eventCookie);

        [PreserveSig] int GetOptionDescription([MarshalAs(UnmanagedType.LPWStr)] string optionId, out IntPtr value);

        [PreserveSig] int ComprehensiveCheck([MarshalAs(UnmanagedType.LPWStr)] string text, out IEnumSpellingError? value);

        [PreserveSig] int Remove([MarshalAs(UnmanagedType.LPWStr)] string word);
    }

    [ComImport]
    [Guid("803E3BD4-2828-4410-8290-418D1D73C762")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEnumSpellingError
    {
        /// <summary>S_OK with the next error, S_FALSE (and null) at the end.</summary>
        [PreserveSig] int Next(out ISpellingError? value);
    }

    [ComImport]
    [Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISpellingError
    {
        [PreserveSig] int get_StartIndex(out uint value);

        [PreserveSig] int get_Length(out uint value);

        /// <summary>CORRECTIVE_ACTION: 0 none, 1 get suggestions, 2 replace, 3 delete.</summary>
        [PreserveSig] int get_CorrectiveAction(out int value);

        [PreserveSig] int get_Replacement(out IntPtr value);
    }

    /// <summary>The standard IEnumString (objidl.h).</summary>
    [ComImport]
    [Guid("00000101-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEnumString
    {
        [PreserveSig] int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] rgelt, out uint pceltFetched);

        [PreserveSig] int Skip(uint celt);

        [PreserveSig] int Reset();

        [PreserveSig] int Clone(out IEnumString? ppenum);
    }

    /// <summary>Reads an LPWSTR the API allocated and frees it.</summary>
    public static string? TakeString(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(p);
        }
        finally
        {
            Marshal.FreeCoTaskMem(p);
        }
    }

    /// <summary>All the strings of an enumerator (at most <paramref name="max"/>).</summary>
    public static List<string> ReadAll(IEnumString? e, int max = 1000)
    {
        var list = new List<string>();
        if (e == null) return list;
        var one = new IntPtr[1];
        while (list.Count < max)
        {
            one[0] = IntPtr.Zero;
            var hr = e.Next(1, one, out var fetched);
            if (hr != S_OK || fetched == 0)
            {
                if (fetched > 0 && TakeString(one[0]) is { } last) list.Add(last);
                break;
            }
            var s = TakeString(one[0]);
            if (s != null) list.Add(s);
        }
        return list;
    }

    public static void Release(object? com)
    {
        if (com == null) return;
        try
        {
            if (Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidComObjectException)
        {
            // already released
        }
    }
}
