using ACE.Database;
using ACE.Entity.Enum;
using ACE.Server.Network;
using ACE.Shared;
using ACE.Shared.Helpers;
using Microsoft.EntityFrameworkCore.Utilities;
using System.Collections.Generic;

namespace CulinaryTest;

[HarmonyPatch]
public class PatchClass
{
    #region Settings
    const int RETRIES = 10;

    public static Settings Settings = new();
    static string settingsPath => Path.Combine(Mod.ModPath, "Settings.json");
    private FileInfo settingsInfo = new(settingsPath);

    private JsonSerializerOptions _serializeOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private void SaveSettings()
    {
        string jsonString = JsonSerializer.Serialize(Settings, _serializeOptions);

        if (!settingsInfo.RetryWrite(jsonString, RETRIES))
        {
            ModManager.Log($"Failed to save settings to {settingsPath}...", ModManager.LogLevel.Warn);
            Mod.State = ModState.Error;
        }
    }

    private void LoadSettings()
    {
        if (!settingsInfo.Exists)
        {
            ModManager.Log($"Creating {settingsInfo}...");
            SaveSettings();
        }
        else
            ModManager.Log($"Loading settings from {settingsPath}...");

        if (!settingsInfo.RetryRead(out string jsonString, RETRIES))
        {
            Mod.State = ModState.Error;
            return;
        }

        try
        {
            Settings = JsonSerializer.Deserialize<Settings>(jsonString, _serializeOptions);
        }
        catch (Exception)
        {
            ModManager.Log($"Failed to deserialize Settings: {settingsPath}", ModManager.LogLevel.Warn);
            Mod.State = ModState.Error;
            return;
        }
    }
    #endregion

    #region Start/Shutdown
    public void Start()
    {
        //Need to decide on async use
        Mod.State = ModState.Loading;
        LoadSettings();

        Init();

        if (Mod.State == ModState.Error)
        {
            ModManager.DisableModByPath(Mod.ModPath);
            return;
        }

        Mod.State = ModState.Running;
    }

    public void Shutdown()
    {
        //if (Mod.State == ModState.Running)
        //    ClearSpells();

        if (Mod.State == ModState.Error)
            ModManager.Log($"Improper shutdown: {Mod.ModPath}", ModManager.LogLevel.Error);
    }
    #endregion

    const uint CULINARY_ID = 3760;
    const uint CUSTOM_ID = 27; //CULINARY_ID + 100001;
    const FakeBool WellFed = (FakeBool)11000;
    const FakeFloat Satiety = (FakeFloat)11000;
    static Spell customSpell;

    private void Init()
    {
        Spell original = new Spell(CULINARY_ID);
        customSpell = original;

        //Change SpellBase for your version - requires publicizer for access to private Setter
        var sb = original._spellBase.DeepClone();
        sb.Duration = -1;                   //Doesn't expire, must be exactly -1
        sb.Name = "Epicurean's Ecstasy";    //Name sent on cast
        sb.Category += 1;                   //Determines group used in refresh/surpass
        sb.Power++;                         //Also used in surpass
        sb.MetaSpellId = CUSTOM_ID;         //What shows up in the Enchantments on client
        //Neither bitfield effects
        //sb.Bitfield = (uint)SpellFlags.Beneficial;

        //Nothing to do with the 
        var s = original._spell.Clone();
        s.StatModVal = 1000;
        //s.TransferBitfield = (uint)SpellFlags.Beneficial;
        //s.Id = CUSTOM_ID;
        //s.Statm

        customSpell._spellBase = sb;
        customSpell._spell = s;

        ClearSpell();
        if (DatabaseManager.World.spellCache.TryAdd(CUSTOM_ID, s))
            ModManager.Log($"Updated DB Spell: {CUSTOM_ID} - {s.Name}");
        if (DatManager.PortalDat.SpellTable.Spells.TryAdd(CUSTOM_ID, sb))
            ModManager.Log($"Updated SpellBase: {CUSTOM_ID} - {sb.Name}");
    }

    void ClearSpell()
    {

        if (DatabaseManager.World.spellCache.TryRemove(CUSTOM_ID, out var val))
            ModManager.Log($"Removed DB Spell: {CUSTOM_ID} - {val.Name}");
        if (DatManager.PortalDat.SpellTable.Spells.Remove(CUSTOM_ID, out var sb))
            ModManager.Log($"Removed SpellBase: {CUSTOM_ID} - {sb.Name}");
    }

    [CommandHandler("unbuff", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld)]
    public static void Unbuff(Session session, params string[] parameters)
        => session.Player.EnchantmentManager.DispelAllEnchantments();

    [CommandHandler("buff", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld)]
    public static void Buff(Session session, params string[] parameters)
    {
        var p = session.Player;

        //Clear all
        p.EnchantmentManager.DispelAllEnchantments();

        var old = new Spell(CULINARY_ID);


        p.SendMessage($"Casting buff(s):" +
            //$"\nID={old.Id} Cat={old.Category} Power={old.Power} Icon={old.IconID}" +
            $"\nID={customSpell.Id} Cat={customSpell.Category} Power={customSpell.Power} Beneficial={customSpell.IsBeneficial}");

        p.TryCastSpell(customSpell, p);
        //p.TryCastSpell(old, p);
    }
    [CommandHandler("eat", AccessLevel.Developer, CommandHandlerFlag.RequiresWorld)]
    public static void Eat(Session session, params string[] parameters)
    {
        var p = session.Player;

        var current = p.GetProperty(Satiety) ?? 0;
        var next = Math.Min(20, current + 7);
        p.SetProperty(Satiety, next);
        p.SendMessage($"You have {next:0.0} seconds of fullness");
    }


    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.Heartbeat), new Type[] { typeof(double) })]
    public static void PostHeartbeat(double currentUnixTime, ref Player __instance)
    {
        //If you weren't fed check if you are now to apply the buff
        if(__instance.GetProperty(WellFed) ?? false)
        {
            //Take 5 seconds of food away
            var current = Math.Max(0, (__instance.GetProperty(Satiety) ?? 0) - 5);
            __instance.SetProperty(Satiety, current);
            __instance.SendMessage($"{current:0.0} seconds of food left!");

            //Check if you're no longer full and remove buff if so
            if(current <= 0)
            {
                __instance.SetProperty(WellFed, false);
                __instance.EnchantmentManager.Remove(__instance.EnchantmentManager.GetEnchantment(CUSTOM_ID));
            }
        }
        //Check if you got fed and add buff if so
        else if ((__instance.GetProperty(Satiety) ?? 0) > 0)
        {
            __instance.SetProperty(WellFed, true);
            __instance.TryCastSpell(customSpell, __instance);
        }
    }
}

