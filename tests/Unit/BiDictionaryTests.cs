using BidirectionalDict;
using GdUnit4;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Unit;

/// <summary>
/// L-01, L-02: BiDictionary — pure C#, no Godot runtime required.
/// </summary>
[TestSuite]
public class BiDictionaryTests
{
    // L-01 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void LookupBothDirections()
    {
        var dict = new BiDictionary<byte, string>();
        dict.AddOrUpdate(1, "hello");

        AssertThat(dict[1]).IsEqual("hello");
        AssertThat(dict["hello"]).IsEqual((byte)1);
    }

    [TestCase]
    public void Contains_ReturnsTrue_ForBothKeys()
    {
        var dict = new BiDictionary<byte, string>();
        dict.AddOrUpdate(2, "world");

        AssertThat(dict.Contains((byte)2)).IsTrue();
        AssertThat(dict.Contains("world")).IsTrue();
    }

    [TestCase]
    public void Contains_ReturnsFalse_ForMissingKeys()
    {
        var dict = new BiDictionary<byte, string>();

        AssertThat(dict.Contains((byte)99)).IsFalse();
        AssertThat(dict.Contains("missing")).IsFalse();
    }

    // L-02 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void AddOrUpdate_OverwritesFirstKey_UpdatesBothDirections()
    {
        var dict = new BiDictionary<byte, string>();
        dict.AddOrUpdate(1, "alpha");
        dict.AddOrUpdate(1, "beta");  // overwrite key 1 → new value "beta"

        AssertThat(dict[1]).IsEqual("beta");
        AssertThat(dict.Contains("beta")).IsTrue();
    }

    [TestCase]
    public void AddOrUpdate_RemovesOldReverseEntry_WhenKeyOverwritten()
    {
        var dict = new BiDictionary<byte, string>();
        dict.AddOrUpdate(1, "old");
        dict.AddOrUpdate(1, "new");

        // "old" should no longer map back to 1
        AssertThat(dict.Contains("old")).IsFalse();
    }

    [TestCase]
    public void TryRemove_ByFirstKey_RemovesBothDirections()
    {
        var dict = new BiDictionary<byte, string>();
        dict.AddOrUpdate(5, "five");
        bool removed = dict.TryRemove((byte)5);

        AssertThat(removed).IsTrue();
        AssertThat(dict.Contains((byte)5)).IsFalse();
        AssertThat(dict.Contains("five")).IsFalse();
    }

    [TestCase]
    public void TryRemove_BySecondKey_RemovesBothDirections()
    {
        var dict = new BiDictionary<byte, string>();
        dict.AddOrUpdate(7, "seven");
        bool removed = dict.TryRemove("seven");

        AssertThat(removed).IsTrue();
        AssertThat(dict.Contains((byte)7)).IsFalse();
    }

    [TestCase]
    public void Count_ReflectsCorrectEntryCount()
    {
        var dict = new BiDictionary<byte, string>();
        dict.AddOrUpdate(1, "a");
        dict.AddOrUpdate(2, "b");
        dict.AddOrUpdate(3, "c");

        AssertThat(dict.Count).IsEqual(3);
    }

    [TestCase]
    public void IsSynced_IsTrue_WhenBothSidesMatch()
    {
        var dict = new BiDictionary<byte, string>();
        dict.AddOrUpdate(1, "x");
        dict.AddOrUpdate(2, "y");

        AssertThat(dict.IsSynced).IsTrue();
    }
}
