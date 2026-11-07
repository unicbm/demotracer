using System.Collections;
using System.Reflection;
using BotRandomizer;

internal static class TakeoverWearableTests
{
    internal static void Run()
    {
        var applicator = new CosmeticApplicator(null, null!);
        const uint pawn = 0x10007, knifeEntity = 0x20008;
        var knife = new KnifeSelection(507, 43, 0.409f, 711);
        var gloves = new GloveSelection(5032, 10036, 0.356f, 302);
        SetApplied(applicator, "_appliedKnives", "AppliedKnifeCosmetic", 7,
            pawn, knifeEntity, knife, 1001UL, 123U);
        SetApplied(applicator, "_appliedGloves", "AppliedGloveCosmetic", 7,
            pawn, gloves, 1002UL, (ushort)5032, 123U);
        SetApplied(applicator, "_appliedKnives", "AppliedKnifeCosmetic", 8,
            0x10008U, 0x20009U, new KnifeSelection(515, 38, 0.017f, 42), 2001UL, 456U);
        SetApplied(applicator, "_appliedGloves", "AppliedGloveCosmetic", 8,
            0x10008U, new GloveSelection(5034, 10035, 0.3f, 422), 2002UL, (ushort)5034, 456U);

        var captured = applicator.CaptureTakeoverWearables(7, pawn);
        Require(captured.Knife?.DefIndex == 507 && captured.Knife.PaintKit == 43 &&
            captured.Knife.Identity?.ItemId == 1001UL && captured.Knife.Identity.ItemAccountId == 123U &&
            captured.WeaponHandle == knifeEntity, "knife model, paint and identity stay with the source pawn");
        Require(captured.Gloves?.DefIndex == 5032 && captured.Gloves.PaintKit == 10036 &&
            captured.Gloves.Identity?.ItemId == 1002UL && captured.Gloves.Identity.ItemAccountId == 123U,
            "gloves stay with the source pawn, not the other bot or the human");
        Require(applicator.CaptureTakeoverWearables(7, 0x30007).Knife is null &&
            applicator.CaptureTakeoverWearables(7, 0x30007).Gloves is null,
            "reused slots cannot supply the previous pawn's cosmetics");
        applicator.ClearSlot(7);
        Require(applicator.CaptureTakeoverWearables(7, pawn).Knife is null &&
            captured.Knife?.DefIndex == 507 && captured.Gloves?.DefIndex == 5032,
            "the captured handoff survives DTR releasing its write lease");

        var binding = new TakeoverPawnBinding(1, 0x10001, 0x10007, pawn);
        Require(binding.Matches(1, 0x10001, 0x10007, pawn, 0x10007, true, true), "same live takeover");
        Require(!binding.Matches(2, 0x10001, 0x10007, pawn, 0x10007, true, true), "map change");
        Require(!binding.Matches(1, 0x20001, 0x10007, pawn, 0x10007, true, true), "human reconnect");
        Require(!binding.Matches(1, 0x10001, 0x20007, pawn, 0x10007, true, true), "bot replacement");
        Require(!binding.Matches(1, 0x10001, 0x10007, 0x30007, 0x10007, true, true), "human respawn");
        Require(!binding.Matches(1, 0x10001, 0x10007, pawn, 0x10008, true, true), "different source bot");
        Require(!binding.Matches(1, 0x10001, 0x10007, pawn, 0x10007, false, true), "takeover ended");
        Require(!binding.Matches(1, 0x10001, 0x10007, pawn, 0x10007, true, false), "pawn death");
    }

    private static void SetApplied(CosmeticApplicator applicator, string field, string type, int slot, params object[] values)
    {
        var record = typeof(CosmeticApplicator).GetNestedType(type, BindingFlags.NonPublic)!;
        var cache = (IDictionary)typeof(CosmeticApplicator).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(applicator)!;
        cache.Add(slot, Activator.CreateInstance(record, values)!);
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException($"Takeover wearables: {message}");
    }
}
