using System.Text.RegularExpressions;

namespace PhotoMap.Core;

/// <summary>
/// Turns folder and file names into a place-name search ("Kreta 2012" → "Kreta"),
/// or null when a name carries no place (camera numbers, dates, "Vakantie", ...).
/// </summary>
public static partial class NameHints
{
    // Words that describe the occasion or the file, not the place. Dutch, English, Romanian, German.
    static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        // occasions
        "vakantie", "zomervakantie", "wintervakantie", "herfstvakantie", "meivakantie", "voorjaarsvakantie",
        "holiday", "holidays", "vacation", "vacanta", "vacanța", "urlaub", "trip", "reis", "reisje", "uitje",
        "weekend", "weekendje", "dagje", "dag", "day", "days", "zi", "tour", "visit", "bezoek", "excursie",
        "verjaardag", "jarig", "birthday", "feest", "party", "kerst", "kerstmis", "christmas", "craciun",
        "pasen", "easter", "paste", "sinterklaas", "oudjaar", "nieuwjaar", "bruiloft", "wedding", "nunta",
        "doop", "baptism", "botez", "communie", "school", "schoolreis", "kamp", "camp", "camping",
        "familie", "family", "familia", "oma", "opa", "mama", "papa", "mam", "pap", "mom", "dad", "mum",
        "bunica", "bunicul", "bunici", "kids", "kinderen", "children", "copii", "vrienden", "friends", "prieteni",
        // file / camera words
        "foto", "fotos", "foto's", "photo", "photos", "pictures", "picture", "pics", "pic", "img", "image", "images",
        "dsc", "dscn", "dscf", "dcim", "pict", "mvi", "vid", "video", "pxl", "imag", "sam", "wp", "p", "e",
        "whatsapp", "screenshot", "schermafbeelding", "scan", "scans", "copy", "kopie", "edited", "bewerkt",
        "export", "exported", "camera", "iphone", "android", "samsung", "sony", "canon", "nikon", "olympus",
        "panasonic", "fuji", "fujifilm", "gopro", "apple", "album", "albums", "map", "folder", "new", "nieuw",
        "nieuwe", "deel", "part", "misc", "diversen", "overig", "overige", "random", "various", "backup",
        "selectie", "selection", "best", "beste", "mooiste", "print", "afdrukken",
        // months and seasons
        "jan", "januari", "january", "ianuarie", "feb", "februari", "february", "februarie", "mrt", "maart",
        "march", "martie", "apr", "april", "aprilie", "mei", "may", "mai", "jun", "juni", "june", "iunie",
        "jul", "juli", "july", "iulie", "aug", "augustus", "august", "sep", "sept", "september", "septembrie",
        "okt", "oct", "oktober", "october", "octombrie", "nov", "november", "noiembrie", "dec", "december",
        "decembrie", "zomer", "summer", "vara", "winter", "iarna", "lente", "spring", "herfst", "autumn", "fall"
    };

    [GeneratedRegex(@"[\p{L}][\p{L}'’]*")]
    private static partial Regex WordRegex();

    // Small linking words: kept inside a name ("La Palma", "Puerto de la Cruz"), dropped in the retry.
    static readonly HashSet<string> Linking = new(StringComparer.OrdinalIgnoreCase)
    {
        "met", "with", "en", "and", "si", "și", "bij", "at", "in", "naar", "to", "de", "het", "the", "van",
        "of", "la", "le", "din", "een", "a", "op", "aan", "und", "mit", "im", "am", "zu"
    };

    /// <summary>Search text for a folder's last path segment, or null.</summary>
    public static string? FromFolder(string folderName) => Clean(folderName);

    /// <summary>Search text from a file name (without extension), or null. Camera-style names give null.</summary>
    public static string? FromFile(string fileName) => Clean(Path.GetFileNameWithoutExtension(fileName));

    /// <summary>
    /// Searches to try in order: the whole text, then without linking words,
    /// then the longest single words ("Kreta met Mark" → "Kreta met Mark", "Kreta Mark", "Kreta", "Mark").
    /// </summary>
    public static List<string> Candidates(string text, int max = 4)
    {
        var list = new List<string> { text };
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var content = words.Where(w => !Linking.Contains(w)).ToList();
        if (content.Count > 0 && content.Count < words.Length) list.Add(string.Join(' ', content));
        if (content.Count > 1)
            foreach (var w in content.Where(w => w.Length >= 4).OrderByDescending(w => w.Length))
                list.Add(w);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    // "La Palma", "Le Havre", "De Koog", "Den Haag": a leading article that belongs to the name.
    static bool IsArticleOfName(List<string> words) =>
        words.Count > 1 && words[0].Length <= 3 && char.IsUpper(words[0][0]) && char.IsUpper(words[1][0]) &&
        words[0] is "La" or "Le" or "De" or "Den" or "El" or "Los" or "Las" or "Les" or "Het" or "The";

    static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var words = new List<string>();
        foreach (Match m in WordRegex().Matches(name))
        {
            var w = m.Value.Trim('\'', '’');
            if (w.Length < 2 || Generic.Contains(w)) continue;
            words.Add(w);
        }
        // Only linking words left ("en de") means no place.
        if (words.Count == 0 || words.All(w => Linking.Contains(w))) return null;
        while (words.Count > 0 && Linking.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        while (words.Count > 0 && Linking.Contains(words[0]) && !IsArticleOfName(words)) words.RemoveAt(0);
        var text = string.Join(' ', words);
        return text.Length >= 3 ? text : null;
    }
}
