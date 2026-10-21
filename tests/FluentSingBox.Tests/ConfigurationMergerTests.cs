using System.Text.Json.Nodes;
using FluentSingBox.Configuration;

namespace FluentSingBox.Tests;

// Pins the current three-way merge semantics: these tests describe what the merger
// does today, including the cases where generated changes win over manual edits.
public class ConfigurationMergerTests
{
    private static JsonObject Merge(string current, string previous, string next)
    {
        var target = Parse(current);
        ConfigurationMerger.ApplyChanges(target, Parse(previous), Parse(next));
        return target;
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static void AssertJson(string expected, JsonNode actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), $"expected {expected}\nactual   {actual.ToJsonString()}");

    [Fact]
    public void IdenticalGeneratedVersionsLeaveCurrentUntouched()
    {
        var result = Merge("""{"log":{"level":"warn"},"user":true}""", """{"log":{"level":"info"}}""", """{"log":{"level":"info"}}""");
        AssertJson("""{"log":{"level":"warn"},"user":true}""", result);
    }

    [Fact]
    public void NestedObjectFieldsAreAddedRemovedAndModified()
    {
        var result = Merge(
            """{"log":{"level":"info","timestamp":true,"user":1}}""",
            """{"log":{"level":"info","timestamp":true}}""",
            """{"log":{"level":"debug","output":"x"}}""");
        AssertJson("""{"log":{"level":"debug","user":1,"output":"x"}}""", result);
    }

    [Fact]
    public void ArraysWithoutTagsAreReplacedWholesale()
    {
        var result = Merge("""{"a":[1,2,9]}""", """{"a":[1,2]}""", """{"a":[3]}""");
        AssertJson("""{"a":[3]}""", result);
    }

    [Fact]
    public void TaggedArraysMergeByTag()
    {
        var result = Merge(
            """{"outbounds":[{"tag":"user","type":"http"},{"tag":"a","type":"direct","extra":1},{"tag":"b","type":"block"}]}""",
            """{"outbounds":[{"tag":"a","type":"direct"},{"tag":"b","type":"block"}]}""",
            """{"outbounds":[{"tag":"a","type":"socks"},{"tag":"c","type":"dns"}]}""");
        AssertJson("""{"outbounds":[{"tag":"user","type":"http"},{"tag":"a","type":"socks","extra":1},{"tag":"c","type":"dns"}]}""", result);
    }

    [Fact]
    public void TaggedArrayReorderDoesNotReorderCurrent()
    {
        var result = Merge(
            """{"o":[{"tag":"a"},{"tag":"u"},{"tag":"b"}]}""",
            """{"o":[{"tag":"a"},{"tag":"b"}]}""",
            """{"o":[{"tag":"b"},{"tag":"a"}]}""");
        AssertJson("""{"o":[{"tag":"a"},{"tag":"u"},{"tag":"b"}]}""", result);
    }

    [Fact]
    public void DuplicateTagsResolveToTheFirstMatch()
    {
        var result = Merge("""{"o":[{"tag":"x","v":1}]}""", """{"o":[{"tag":"x","v":1}]}""", """{"o":[{"tag":"x","v":2},{"tag":"x","v":3}]}""");
        AssertJson("""{"o":[{"tag":"x","v":2}]}""", result);
    }

    [Fact]
    public void TaggedEntryDeletedByUserReturnsOnlyWhenItsGeneratedVersionChanges()
    {
        var result = Merge(
            """{"o":[]}""",
            """{"o":[{"tag":"a","v":1},{"tag":"b","v":1}]}""",
            """{"o":[{"tag":"a","v":1},{"tag":"b","v":2}]}""");
        AssertJson("""{"o":[{"tag":"b","v":2}]}""", result);
    }

    [Fact]
    public void AddedTaggedEntryReplacesAnExistingEntryWithTheSameTag()
    {
        var result = Merge(
            """{"o":[{"tag":"n","v":0},{"tag":"a"}]}""",
            """{"o":[{"tag":"a"}]}""",
            """{"o":[{"tag":"a"},{"tag":"n","v":1}]}""");
        AssertJson("""{"o":[{"tag":"a"},{"tag":"n","v":1}]}""", result);
    }

    [Fact]
    public void EmptyGeneratedArrayIsMergedAsTagged()
    {
        Assert.Equal("""{"o":[{"tag":"u"},{"tag":"t"}]}""",
            Merge("""{"o":[{"tag":"u"}]}""", """{"o":[]}""", """{"o":[{"tag":"t"}]}""").ToJsonString());
        Assert.Equal("""{"o":[{"tag":"u"}]}""",
            Merge("""{"o":[{"tag":"a"},{"tag":"u"}]}""", """{"o":[{"tag":"a"}]}""", """{"o":[]}""").ToJsonString());
    }

    private const string Sniff = """{"action":"sniff"}""";
    private const string DomainA = """{"domain":["a"],"outbound":"direct"}""";
    private const string DomainAB = """{"domain":["a","b"],"outbound":"direct"}""";
    private const string Private = """{"ip_is_private":true,"outbound":"direct"}""";
    private const string Blocked = """{"domain":["n"],"outbound":"block"}""";
    private const string UserRule = """{"domain":["user"],"outbound":"proxy"}""";

    private static string Rules(params string[] rules) => """{"route":{"rules":[""" + string.Join(",", rules) + "]}}";

    [Fact]
    public void RulesReplaceUnmodifiedGeneratedRulesInPlaceAndKeepUserRules()
    {
        var result = Merge(Rules(UserRule, Sniff, DomainA, Private), Rules(Sniff, DomainA, Private), Rules(Sniff, DomainAB, Private, Blocked));
        AssertJson(Rules(UserRule, Sniff, DomainAB, Private, Blocked), result);
    }

    [Fact]
    public void RulesKeepAUserModifiedGeneratedRuleAndInsertTheNewVersionAtTheTop()
    {
        const string modified = """{"domain":["a"],"outbound":"proxy"}""";
        var result = Merge(Rules(modified), Rules(DomainA), Rules(DomainAB));
        AssertJson(Rules(DomainAB, modified), result);
    }

    [Fact]
    public void RulesAnchorBeforeTheNextSurvivingSiblingWhenNoPredecessorSurvives()
    {
        var result = Merge(Rules(UserRule, Private), Rules(Sniff, Private), Rules(Blocked, Private));
        AssertJson(Rules(UserRule, Blocked, Private), result);
    }

    [Fact]
    public void RulesRemovedFromTheGeneratedVersionAreRemoved()
    {
        var result = Merge(Rules(Sniff, DomainA, UserRule), Rules(Sniff, DomainA), Rules(Sniff));
        AssertJson(Rules(Sniff, UserRule), result);
    }

    [Fact]
    public void RulesAlreadyPresentInCurrentAreNotAddedTwice()
    {
        var result = Merge(Rules(Blocked, Sniff), Rules(Sniff), Rules(Sniff, Blocked));
        AssertJson(Rules(Blocked, Sniff), result);
    }

    [Fact]
    public void RulesMergeAlsoAppliesToNestedDnsRules()
    {
        var result = Merge(
            """{"dns":{"rules":[{"domain":["u"],"server":"x"},{"domain":["a"],"server":"d"}]}}""",
            """{"dns":{"rules":[{"domain":["a"],"server":"d"}]}}""",
            """{"dns":{"rules":[{"domain":["a","b"],"server":"d"}]}}""");
        // The replaced rule had no surviving neighbour in the generated list, so it goes first.
        AssertJson("""{"dns":{"rules":[{"domain":["a","b"],"server":"d"},{"domain":["u"],"server":"x"}]}}""", result);
    }

    [Fact]
    public void NullInTheGeneratedVersionSetsTheFieldToNull()
    {
        var result = Merge("""{"a":1,"b":2}""", """{"a":1,"b":2}""", """{"a":null,"b":2}""");
        Assert.True(result.ContainsKey("a"));
        Assert.Null(result["a"]);
    }

    [Fact]
    public void FieldMissingFromTheGeneratedVersionIsRemoved()
    {
        var result = Merge("""{"a":1,"b":2}""", """{"a":1,"b":2}""", """{"b":2}""");
        AssertJson("""{"b":2}""", result);
    }

    [Fact]
    public void EmptyDocumentsChangeNothing()
    {
        AssertJson("""{"x":1}""", Merge("""{"x":1}""", "{}", "{}"));
        AssertJson("""{"x":1,"a":[]}""", Merge("""{"x":1,"a":[]}""", """{"a":[]}""", """{"a":[]}"""));
    }

    [Fact]
    public void DeeplyNestedObjectsAndArraysMergeTogether()
    {
        var result = Merge(
            """{"dns":{"servers":[{"tag":"d1","address":"1.1.1.1","opts":{"x":1,"mine":true}},{"tag":"mine"}],"rules":[{"server":"d1","domain":["a"]}],"strategy":"ipv4_only"}}""",
            """{"dns":{"servers":[{"tag":"d1","address":"1.1.1.1","opts":{"x":1}}],"rules":[{"server":"d1","domain":["a"]}],"strategy":"ipv4_only"}}""",
            """{"dns":{"servers":[{"tag":"d1","address":"1.1.1.1","opts":{"x":2}}],"rules":[{"server":"d1","domain":["b"]}],"strategy":"prefer_ipv4"}}""");
        AssertJson("""{"dns":{"servers":[{"tag":"d1","address":"1.1.1.1","opts":{"x":2,"mine":true}},{"tag":"mine"}],"rules":[{"server":"d1","domain":["b"]}],"strategy":"prefer_ipv4"}}""", result);
    }

    [Fact]
    public void KeyDeletedByUserStaysDeletedWhileItsGeneratedValueIsUnchanged()
    {
        var result = Merge("""{"c":2}""", """{"a":{"b":1},"c":1}""", """{"a":{"b":1},"c":2}""");
        AssertJson("""{"c":2}""", result);
    }

    [Fact]
    public void KeyDeletedByUserReturnsWhenItsGeneratedValueChanges()
    {
        var result = Merge("{}", """{"a":{"b":1}}""", """{"a":{"b":2}}""");
        AssertJson("""{"a":{"b":2}}""", result);
    }

    [Fact]
    public void NewGeneratedKeyOverwritesAnExistingValueInCurrent()
    {
        var result = Merge("""{"n":5}""", "{}", """{"n":1}""");
        AssertJson("""{"n":1}""", result);
    }

    [Fact]
    public void ReplacementValuesAreClonedFromTheGeneratedDocument()
    {
        var next = Parse("""{"a":{"b":1}}""");
        var current = Parse("{}");
        ConfigurationMerger.ApplyChanges(current, Parse("{}"), next);
        next["a"]!["b"] = 99;
        AssertJson("""{"a":{"b":1}}""", current);
    }
}
