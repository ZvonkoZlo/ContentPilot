using System.Security.Cryptography;
using System.Text;

namespace ContentPilot.Application.ContentMemory;

/// <summary>
/// A 64-bit locality-sensitive hash over word tokens. Near-duplicate texts land within a
/// small Hamming distance of each other.
/// <para>
/// This exists so "do not repeat what you published last month" is a computation rather
/// than an instruction the model may quietly ignore. Asking a model whether it is repeating
/// itself is asking the least reliable component to audit its own memory.
/// </para>
/// </summary>
public static class SimHash
{
    /// <summary>
    /// Twenty, derived from measurement rather than taste.
    /// <para>
    /// Against a corpus of hand-written rephrasings and genuinely distinct topics from this
    /// domain, rephrasings measured 0-18 bits apart and different subjects 22-40. Twenty
    /// sits in that gap.
    /// </para>
    /// <para>
    /// The margin is two bits on either side, which is thin, and the corpus is small. Treat
    /// this as calibrated, not settled: re-derive it against real strategist output once
    /// there is some, and re-derive it again if tokenisation or stemming changes, since both
    /// move every distance.
    /// </para>
    /// </summary>
    public const int NearDuplicateThreshold = 20;

    /// <summary>
    /// Tokens are compared on their first few characters. Five keeps "booking" and "books"
    /// apart while collapsing "cancel"/"cancels" and "termin"/"termina".
    /// </summary>
    private const int StemLength = 5;

    public static long Compute(string text)
    {
        var tokens = Tokenise(text);

        if (tokens.Count == 0)
        {
            return 0;
        }

        // One weight per bit position. A bit is set when more tokens voted for it than
        // against, which is what makes the result stable under small edits.
        var weights = new int[64];

        foreach (var (token, count) in tokens)
        {
            var hash = HashToken(token);

            for (var bit = 0; bit < 64; bit++)
            {
                var isSet = (hash & (1UL << bit)) != 0;
                weights[bit] += isSet ? count : -count;
            }
        }

        ulong result = 0;

        for (var bit = 0; bit < 64; bit++)
        {
            if (weights[bit] > 0)
            {
                result |= 1UL << bit;
            }
        }

        return unchecked((long)result);
    }

    public static int Distance(long left, long right) =>
        System.Numerics.BitOperations.PopCount(unchecked((ulong)(left ^ right)));

    public static bool IsNearDuplicate(long left, long right, int threshold = NearDuplicateThreshold) =>
        Distance(left, right) <= threshold;

    /// <summary>
    /// Lowercased, stripped of diacritics and stop words, and counted.
    /// <para>
    /// Diacritics are folded because the brands here write Croatian: "termin" and "termín"
    /// must not read as different subjects. Stop words are dropped in both languages, since
    /// otherwise two unrelated sentences share most of their tokens and every comparison
    /// drifts toward "similar".
    /// </para>
    /// </summary>
    private static Dictionary<string, int> Tokenise(string text)
    {
        var folded = Fold(text);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length < 3)
            {
                current.Clear();
                return;
            }

            var token = current.ToString();
            current.Clear();

            if (StopWords.Contains(token))
            {
                return;
            }

            // Compared on a prefix, not the whole word. Without this, "client" and
            // "clients" are unrelated tokens and a plain rephrasing measures further apart
            // than two different subjects. A prefix handles English plurals and verb forms
            // and Croatian's much richer inflection with one rule and no stemmer per
            // language - crude, but crude in a direction that is easy to reason about.
            var stem = token.Length > StemLength ? token[..StemLength] : token;

            counts[stem] = counts.GetValueOrDefault(stem) + 1;
        }

        foreach (var c in folded)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                current.Append(c);
            }
            else
            {
                Flush();
            }
        }

        Flush();

        return counts;
    }

    /// <summary>
    /// Folds accents with an explicit table rather than Unicode normalisation.
    /// <para>
    /// The solution builds with <c>InvariantGlobalization</c>, under which
    /// <c>String.Normalize</c> is a silent no-op — diacritics survive, and "spavas" and
    /// "spavaš" hash as unrelated words. That is how this was found.
    /// </para>
    /// <para>
    /// An explicit table is the better answer regardless. These hashes are persisted and
    /// compared across deployments, so folding must not depend on whether ICU happens to be
    /// present in a given container: the same topic has to hash the same way everywhere.
    /// </para>
    /// </summary>
    private static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var c in text.ToLowerInvariant())
        {
            builder.Append(Accents.TryGetValue(c, out var folded) ? folded : c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Croatian first, since that is what these brands write, then the common Latin-1
    /// accents so a second market does not silently degrade.
    /// </summary>
    private static readonly Dictionary<char, char> Accents = new()
    {
        ['č'] = 'c', ['ć'] = 'c', ['ž'] = 'z', ['š'] = 's', ['đ'] = 'd',
        ['á'] = 'a', ['à'] = 'a', ['â'] = 'a', ['ä'] = 'a', ['ã'] = 'a', ['å'] = 'a',
        ['é'] = 'e', ['è'] = 'e', ['ê'] = 'e', ['ë'] = 'e',
        ['í'] = 'i', ['ì'] = 'i', ['î'] = 'i', ['ï'] = 'i',
        ['ó'] = 'o', ['ò'] = 'o', ['ô'] = 'o', ['ö'] = 'o', ['õ'] = 'o',
        ['ú'] = 'u', ['ù'] = 'u', ['û'] = 'u', ['ü'] = 'u',
        ['ý'] = 'y', ['ÿ'] = 'y', ['ñ'] = 'n', ['ç'] = 'c',
    };

    private static ulong HashToken(string token)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(token), digest);

        return BitConverter.ToUInt64(digest);
    }

    /// <summary>
    /// English and Croatian. Kept deliberately short: over-aggressive removal strips the
    /// words that actually distinguish two topics.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "you", "your", "with", "that", "this", "from", "have", "has",
        "are", "was", "were", "not", "but", "can", "will", "how", "why", "what", "when",
        "who", "all", "any", "out", "get", "got", "its", "his", "her", "our", "their",
        "kada", "kako", "sto", "koji", "koja", "koje", "sve", "svi", "ali", "ili", "pa",
        "nije", "jest", "biti", "bez", "pod", "nad", "kod", "vas", "nas", "moze", "mora",
    };
}
