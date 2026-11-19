using MosaiCal.Services;
using Xunit;

namespace MosaiCal.Tests;

public sealed class UniversalTests
{
    internal UniRegexService Service { get; } = new("Arizona State University");

    [Theory]
    [InlineData("Homework 1 [CSE101 1234A]", "CSE101 1234A")]
    [InlineData("Lab [Part 1] [CSE101 1234A]", "CSE101 1234A")]
    [InlineData("Read ch. [3] and [4] [MAT265 5678B]", "MAT265 5678B")]
    [InlineData("Homework 1 [CSE101 1234A]  ", "CSE101 1234A")]
    [InlineData("Office hours", "")]
    [InlineData("", "")]
    public void GetSectionInfo_UsesLastBracketGroup(string input, string expected)
    {
        Assert.Equal(expected, Service.GetSectionInfo(input));
    }

    [Theory]
    [InlineData("Homework 1 [CSE101 1234A]", "Homework 1")]
    [InlineData("Lab [Part 1] [CSE101 1234A]", "Lab [Part 1]")]
    [InlineData("Read ch. [3] and [4] [MAT265 5678B]", "Read ch. [3] and [4]")]
    [InlineData("[Optional] Quiz [SER316 9999C]", "[Optional] Quiz")]
    [InlineData("Office hours", "Office hours")]
    [InlineData("", "")]
    public void GetAssignmentName_KeepsBracketsInName(string input, string expected)
    {
        Assert.Equal(expected, Service.GetAssignmentName(input));
    }
}
