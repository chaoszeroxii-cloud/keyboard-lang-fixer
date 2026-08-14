// ---------------------------------------------------------------------------
//  Windows' own spell checker, used as a STOP signal when working out how far
//  back a mistyped run goes.
//
//  What it is good for and what it is not, measured rather than assumed:
//
//    - "is this a real English word" is reliable. Please, read, hello, world,
//      the, quick, send, file, test, and, then all come back clean, so a
//      correctly spelled word is solid evidence the user meant to type it and
//      the run of mistyped text ends there.
//
//    - "is this misspelled" does NOT mean "typed on the wrong layout".
//      github, powershell, getUserId, src and png are all reported misspelled.
//      Extending a run on that basis alone would eat perfectly good text, which
//      is why misspelling only ever allows the walk to continue and never
//      forces it, and why the word limit still applies.
//
//    - words containing digits or punctuation are skipped by the checker
//      entirely, so "-v[86I" reads as clean. That makes the run stop early:
//      it converts less than it could, which is the harmless direction.
//
//  Only English is available in practice (th-TH is not supported), so this is
//  consulted only when the source layout is the one producing Latin letters.
// ---------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KbFix
{
    [ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISpellingError
    {
        uint StartIndex { get; }
        uint Length { get; }
        int CorrectiveAction { get; }
        string Replacement { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumSpellingError
    {
        [PreserveSig] int Next([MarshalAs(UnmanagedType.Interface)] out ISpellingError value);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISpellChecker
    {
        string LanguageTag { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
        [return: MarshalAs(UnmanagedType.Interface)]
        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
    }

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISpellCheckerFactory
    {
        [return: MarshalAs(UnmanagedType.Interface)] object get_SupportedLanguages();
        [return: MarshalAs(UnmanagedType.Bool)] bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
        [return: MarshalAs(UnmanagedType.Interface)] ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    /// Decides whether a word looks like something the user meant to type, which
    /// is what ends a run of wrong-layout text. Kept as an interface so the
    /// tests can drive the walk with a fixed vocabulary instead of whatever
    /// dictionary happens to be installed.
    internal interface IWordJudge
    {
        /// True when the word is a real word of the source layout's language, so
        /// the run of mistyped text stops before it.
        bool LooksIntentional(string word);
    }

    internal sealed class SpellWordJudge : IWordJudge
    {
        private static readonly Guid FactoryClsid = new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC");

        private readonly ISpellChecker _checker;
        private readonly Dictionary<string, bool> _cache = new Dictionary<string, bool>(StringComparer.Ordinal);

        public bool Available { get { return _checker != null; } }
        public string LanguageTag { get; private set; }

        private SpellWordJudge(ISpellChecker checker, string tag)
        {
            _checker = checker;
            LanguageTag = tag;
        }

        /// Returns null when no usable dictionary is installed; callers then fall
        /// back to taking a single word.
        public static SpellWordJudge TryCreate()
        {
            try
            {
                Type t = Type.GetTypeFromCLSID(FactoryClsid);
                if (t == null) return null;
                ISpellCheckerFactory factory = (ISpellCheckerFactory)Activator.CreateInstance(t);

                foreach (string tag in new string[] { "en-US", "en-GB", "en" })
                {
                    bool supported;
                    try { supported = factory.IsSupported(tag); }
                    catch { continue; }
                    if (!supported) continue;
                    ISpellChecker checker = factory.CreateSpellChecker(tag);
                    if (checker != null) return new SpellWordJudge(checker, tag);
                }
            }
            catch { }
            return null;
        }

        public bool LooksIntentional(string word)
        {
            if (string.IsNullOrEmpty(word) || _checker == null) return false;

            bool cached;
            if (_cache.TryGetValue(word, out cached)) return cached;

            bool intentional;
            try
            {
                IEnumSpellingError errors = _checker.Check(word);
                ISpellingError first;
                int hr = errors.Next(out first);
                bool misspelled = (hr == 0 && first != null);
                intentional = !misspelled;
            }
            catch
            {
                // A checker that throws must not make the walk swallow text, so
                // treat the word as intentional and stop there.
                intentional = true;
            }

            // The same handful of words comes up over and over while someone is
            // typing; the cache keeps repeated triggers off the COM call.
            if (_cache.Count < 2000) _cache[word] = intentional;
            return intentional;
        }
    }
}
