using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MosaiCal.Services;

class UniParser
{
    public required Func<string, string> SectionInfo { get; init; }
    public required Func<string, string> AssignmentName { get; init; }
    public required Func<string, string> ClassName { get; init; }
    public required Func<string, string> SectionName { get; init; }
}

class UniRegexService
{
    private readonly Dictionary<string, UniParser> _uniParsers;
    private readonly UniParser _defaultParser;

    private const string GeneralKey = "General";

    private string setUniversity;
    public UniRegexService(string universitySetting) {

        setUniversity = universitySetting ?? string.Empty;

        _defaultParser = new UniParser
        {
            SectionInfo = SectionInfoUniversal,
            AssignmentName = AssignmentNameUniversal,
            ClassName = GetDefaultCN,
            SectionName = _ => string.Empty
        };

        // Try to keep these in alphabetical order for the sake of maintainability pls
        _uniParsers = new Dictionary<string, UniParser>
        {
            ["Arizona State University"] = new UniParser
            {
                SectionInfo = SectionInfoUniversal,
                AssignmentName = AssignmentNameUniversal,
                ClassName = GetAzStUniCN,
                SectionName = GetAzStUniSN
            },
            ["General"] = new UniParser
            {
                SectionInfo = SectionInfoUniversal,
                AssignmentName = AssignmentNameUniversal,
                ClassName = GetDefaultCN,
                SectionName = _ => string.Empty
            },
            ["Ohio State University"] = new UniParser
            {
                SectionInfo = SectionInfoUniversal,
                AssignmentName = AssignmentNameUniversal,
                ClassName = GetOhioStUniCN,
                SectionName = GetOhioStUniSN
            },
            ["University of Washington"] = new UniParser
            {
                SectionInfo = SectionInfoUniversal,
                AssignmentName = AssignmentNameUniversal,
                ClassName = GetUWStUniCN,
                SectionName = GetUWStUniSN
            }
        };
    }

    public string University => setUniversity;

    public static IReadOnlyList<string> GetSupportedSchools()
    {
        return new UniRegexService(string.Empty)._uniParsers.Keys
            .Where(name => name != GeneralKey)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private UniParser GetParser()
    {
        return _uniParsers.TryGetValue(setUniversity, out var parser)
            ? parser
            : _defaultParser;
    }

    private string MatchOrEmpty(string input, string pattern)
    {
        var match = Regex.Match(input, pattern);

        return match.Success
            ? match.Value.Trim()
            : String.Empty;
    }

    private string MatchOrInput(string input, string pattern)
    {
        var match = Regex.Match(input, pattern);

        return match.Success
            ? match.Value.Trim()
            : input.Trim();
    }

    public string GetSectionInfo(string summary)
    {
        return GetParser().SectionInfo(summary);
    }

    public string GetAssignmentName(string summary)
    {
        return GetParser().AssignmentName(summary);
    }

    public string GetClassName(string sectionInfo)
    {
        return GetParser().ClassName(sectionInfo);
    }
    
    public string GetSectionName(string sectionInfo)
    {
        return GetParser().SectionName(sectionInfo);
    }

    /* NOTE FOR CONTRIBUTORS:
        * Certain universities may have edge cases for section naming. We will keep the definition
        * to be lambda functions; full on functions can be added if more logic is needed down the line.
        */

    // Universal
    // Canvas appends the section as the LAST bracket group, so brackets inside the
    // assignment name itself (e.g. "Lab [Part 1] [CSE 101 AB1234]") are kept in the name.
    // Summaries with no trailing bracket group fall back to the whole summary as the name.
    private const string CanvasSummaryPattern = @"^(?<name>.*?)\s*\[(?<section>[^\[\]]*)\]\s*$";

    private string SectionInfoUniversal(string summary) => Regex.Match(summary, CanvasSummaryPattern).Groups["section"].Value.Trim();
    private string AssignmentNameUniversal(string summary)
    {
        var match = Regex.Match(summary, CanvasSummaryPattern);
        return match.Success ? match.Groups["name"].Value.Trim() : summary.Trim();
    }

    // Generalized Regex; deals with 3-4
    private string GetGeneralCN(string sectionInfo) => Regex.Match(sectionInfo, @"[A-Z]{3,5} ?[0-9]{3}[a-zA-Z]{0,3}").Value.Trim();

    // Default CN for unsupported universities
    private string GetDefaultCN(string sectionInfo) => sectionInfo;

    // Arizona State University
    private string GetAzStUniCN(string sectionInfo) => MatchOrInput(sectionInfo, @"[A-Z]{3}[0-9]{3}");
    private string GetAzStUniSN(string sectionInfo) => MatchOrEmpty(sectionInfo, @"[0-9]{4}(.*?)[ABC]");

    // Ohio State University
    private string GetOhioStUniCN(string sectionInfo) => MatchOrInput(sectionInfo, @"[A-Z]{3,} [0-9]{4}(?:\.[0-9]{2})?(?:[A-Z])?");
    private string GetOhioStUniSN(string sectionInfo) => MatchOrEmpty(sectionInfo, @"(?<=\s)[A-Z]{2}[0-9]{4}");

    // University of Washington
    private string GetUWStUniCN(string sectionInfo) => MatchOrInput(sectionInfo, @"^[A-Z][A-Z& ]*?\s+\d{3}(?=\s+[A-Z][A-Z0-9]?$)");
    private string GetUWStUniSN(string sectionInfo) => MatchOrEmpty(sectionInfo, @"(?<=\s)[A-Z][A-Z0-9]?$");
}
