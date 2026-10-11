using System.Text;
using Harmonia.Runtime;
using Xunit;

namespace Harmonia.Tests.Runtime;

public sealed class SayScriptTests
{
    private const string Include = "COMPARE_STRING_INCLUDE";
    private const string Equal = "COMPARE_STRING_EQUAL";

    private static LuaTestFunction Say() => new LuaTestFunction(4).Opening();

    private static SayScriptResult Read(LuaTestFunction say) => SayScript.Read(LuaTestFunction.Script(say));

    [Fact]
    public void A_phrase_is_read_with_its_step_and_every_target_of_that_step()
    {
        var read = Read(Say()
            .IfStep("SEQ_2")
            .IfTarget("ACTOR2").Compare("TEXT_TEST_00001_SAYTODO_000_016", Include)
            .IfTarget("EOBJECT0").Compare("TEXT_TEST_00001_SAYTODO_000_016", Include)
            .IfStep("SEQ_5")
            .IfTarget("ACTOR1").Compare("TEXT_TEST_00001_SYSTEM_000_032", Equal));

        Assert.Equal(SayScriptKind.Recognized, read.Kind);
        Assert.Equal(2, read.Phrases.Count);
        Assert.Equal("TEXT_TEST_00001_SAYTODO_000_016", read.Phrases[0].TextKey);
        Assert.Equal("SEQ_2", read.Phrases[0].Sequence);
        Assert.Equal(["ACTOR2", "EOBJECT0"], read.Phrases[0].Targets);
        Assert.False(read.Phrases[0].Exact);
        Assert.Equal("SEQ_5", read.Phrases[1].Sequence);
        Assert.Equal(["ACTOR1"], read.Phrases[1].Targets);
        Assert.True(read.Phrases[1].Exact);
    }

    [Fact]
    public void A_comparison_without_a_mode_is_exact_as_in_the_game()
    {
        var read = Read(Say().IfStep("SEQ_1").IfTarget("ACTOR0").Compare("TEXT_TEST_00001_SAY_000", null));

        Assert.Equal(SayScriptKind.Recognized, read.Kind);
        Assert.True(Assert.Single(read.Phrases).Exact);
    }

    [Fact]
    public void A_script_without_the_function_takes_nothing_from_the_chat()
    {
        Assert.Equal(SayScriptKind.None, SayScript.Read(LuaTestFunction.Script(Say(), "IsAcceptEvent")).Kind);
        Assert.Equal(SayScriptKind.None, SayScript.Read([]).Kind);
    }

    [Fact]
    public void Anything_but_the_known_shape_is_unknown()
    {
        // No step, no target, another text, another table, another mode.
        Assert.Equal(SayScriptKind.Unknown, Read(Say().IfTarget("ACTOR0").Compare("TEXT_A", Include)).Kind);
        Assert.Equal(SayScriptKind.Unknown, Read(Say().IfStep("SEQ_1").Compare("TEXT_A", Include)).Kind);
        Assert.Equal(SayScriptKind.Unknown, Read(Say().IfStep("SEQ_1").IfTarget("ACTOR0").Compare("TEXT_A", Include, textRegister: 4)).Kind);
        Assert.Equal(SayScriptKind.Unknown, Read(Say().IfStep("SEQ_1").IfTarget("ACTOR0").Compare("NAME_A", Include)).Kind);
        Assert.Equal(SayScriptKind.Unknown, Read(Say().IfStep("SEQ_1").IfTarget("ACTOR0").Compare("TEXT_A", "COMPARE_STRING_PREFIX")).Kind);

        // A step that is not a step, "not equal", a call and an instruction
        // the scripts do not use, another parameter count.
        Assert.Equal(SayScriptKind.Unknown, Read(Say().IfStep("STEP_1").IfTarget("ACTOR0").Compare("TEXT_A", Include)).Kind);
        Assert.Equal(SayScriptKind.Unknown, Read(Say().IfStep("SEQ_1").Field(7, "ACTOR0").Abc(LuaChunk.OpEq, 1, 6, 7).Jump().Compare("TEXT_A", Include)).Kind);
        Assert.Equal(SayScriptKind.Unknown, Read(Say().Method(7, 0, "GetQuestUI8AL").Call(7, 0).IfStep("SEQ_1").IfTarget("ACTOR0").Compare("TEXT_A", Include)).Kind);
        Assert.Equal(SayScriptKind.Unknown, Read(Say().Abc(12, 7, 5, 6).IfStep("SEQ_1").IfTarget("ACTOR0").Compare("TEXT_A", Include)).Kind);
        Assert.Equal(SayScriptKind.Unknown, Read(new LuaTestFunction(3).Opening().IfStep("SEQ_1").IfTarget("ACTOR0").Compare("TEXT_A", Include)).Kind);
    }

    [Fact]
    public void The_step_must_be_the_players_and_the_object_the_targets()
    {
        var step = new LuaTestFunction(4)
            .Method(5, 2, "GetQuestSequence").Call(5, 0)
            .Method(6, 2, "GetBaseId").Call(6, 0)
            .IfStep("SEQ_1").IfTarget("ACTOR0").Compare("TEXT_A", Include);
        var target = new LuaTestFunction(4)
            .Method(5, 1, "GetQuestSequence").Call(5, 0)
            .Method(6, 1, "GetBaseId").Call(6, 0)
            .IfStep("SEQ_1").IfTarget("ACTOR0").Compare("TEXT_A", Include);

        Assert.Equal(SayScriptKind.Unknown, Read(step).Kind);
        Assert.Equal(SayScriptKind.Unknown, Read(target).Kind);
    }

    [Fact]
    public void Text_compared_outside_the_function_makes_the_script_unknown()
    {
        var say = Say().IfStep("SEQ_1").IfTarget("ACTOR0").Compare("TEXT_A", Include);
        var other = Say().IfStep("SEQ_1").IfTarget("ACTOR0").Compare("TEXT_B", Include);

        Assert.Equal(SayScriptKind.Recognized, SayScript.Read(LuaTestFunction.Script(say)).Kind);
        Assert.Equal(SayScriptKind.Unknown, SayScript.Read(LuaTestFunction.Script(say, other: other)).Kind);
    }

    [Fact]
    public void Damaged_scripts_are_unknown_and_never_throw()
    {
        var script = LuaTestFunction.Script(Say().IfStep("SEQ_1").IfTarget("ACTOR0").Compare("TEXT_A", Include));
        for (var length = 0; length < script.Length; length++)
        {
            var kind = SayScript.Read(script.AsSpan(0, length)).Kind;
            Assert.NotEqual(SayScriptKind.Recognized, kind);
        }

        var notLua = Encoding.ASCII.GetBytes("return " + SayScript.Function);
        Assert.Equal(SayScriptKind.Unknown, SayScript.Read(notLua).Kind);

        var random = new Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var damaged = (byte[])script.Clone();
            damaged[random.Next(12, damaged.Length)] = (byte)random.Next(256);
            _ = SayScript.Read(damaged);
        }
    }
}
