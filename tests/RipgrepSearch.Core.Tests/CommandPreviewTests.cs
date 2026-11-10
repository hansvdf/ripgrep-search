using RipgrepSearch;

internal static partial class Tests
{
    private static void CommandPreviewTests()
    {
        Check(SearchCommandFormatter.Format("rg", ["--json", "needle", "/folder"]) == "rg --json needle /folder", "plain preview retains v0.10.0 spacing");
        Check(SearchCommandFormatter.Format("rga", ["", "x y", "a\"b", @"C:\a b", @"C:\plain"]) ==
            "rga \"\" \"x y\" \"a\\\"b\" \"C:\\\\a b\" C:\\plain", "preview retains empty/whitespace/quote/backslash escaping");
        Check(SearchCommandFormatter.Format("rg", ["café😀", "café 😀"]) == "rg café😀 \"café 😀\"", "Unicode display quoting");
        Check(SearchCommandFormatter.Format("rg", ["word&echo", "-pattern"]) == "rg word&echo -pattern", "display quoting is not a shell escaping algorithm");
        Check(SearchCommandFormatter.Format("rg", []) == "rg ", "empty argument preview preserves historical trailing space");
    }
}
