using Content.Shared.Atmos;
using Content.Shared.Random;
using Robust.Shared.Utility;
using System.Linq;

namespace Content.Server.Botany;

[DataDefinition]
public sealed partial class PlantMutationCategoryState
{
    [DataField] public PlantMutationCategory Category;
    [DataField] public int IntValue;
    [DataField] public float FloatValue;
    [DataField] public float SecondaryFloatValue;
    [DataField] public bool BoolValue;
    [DataField] public bool SecondaryBoolValue;
    [DataField] public bool TertiaryBoolValue;
    [DataField] public bool QuaternaryBoolValue;
    [DataField] public bool QuinaryBoolValue;
    [DataField] public HarvestType HarvestValue;
    [DataField] public string Name = string.Empty;
    [DataField] public string Noun = string.Empty;
    [DataField] public string PacketName = string.Empty;
    [DataField] public string DisplayName = string.Empty;
    [DataField] public string PacketPrototype = string.Empty;
    [DataField] public bool Mysterious;
    [DataField] public ResPath PlantRsi = default!;
    [DataField] public string PlantIconState = string.Empty;
    [DataField] public string? SplatPrototype;
    [DataField] public List<string> ProductPrototypes = new();
    [DataField] public List<string> MutationPrototypes = new();
    [DataField] public Dictionary<string, SeedChemQuantity> Chemicals = new();
    [DataField] public Dictionary<Gas, float> Gasses = new();
    [DataField] public List<RandomPlantMutation> MutationEntries = new();

    public PlantMutationCategoryState Clone()
    {
        return new PlantMutationCategoryState
        {
            Category = Category,
            IntValue = IntValue,
            FloatValue = FloatValue,
            SecondaryFloatValue = SecondaryFloatValue,
            BoolValue = BoolValue,
            SecondaryBoolValue = SecondaryBoolValue,
            TertiaryBoolValue = TertiaryBoolValue,
            QuaternaryBoolValue = QuaternaryBoolValue,
            QuinaryBoolValue = QuinaryBoolValue,
            HarvestValue = HarvestValue,
            Name = Name,
            Noun = Noun,
            PacketName = PacketName,
            DisplayName = DisplayName,
            PacketPrototype = PacketPrototype,
            Mysterious = Mysterious,
            PlantRsi = PlantRsi,
            PlantIconState = PlantIconState,
            SplatPrototype = SplatPrototype,
            ProductPrototypes = new List<string>(ProductPrototypes),
            MutationPrototypes = new List<string>(MutationPrototypes),
            Chemicals = new Dictionary<string, SeedChemQuantity>(Chemicals),
            Gasses = new Dictionary<Gas, float>(Gasses),
            MutationEntries = new List<RandomPlantMutation>(MutationEntries),
        };
    }
}

public enum PlantMutationCategory : byte
{
    Yield,
    Potency,
    Harvest,
    ContainedSubstances,
    ConsumedGas,
    EmittedGas,
    Lifespan,
    Maturation,
    Production,
    Stages,
    Endurance,
    NutrientUsage,
    WaterUsage,
    IdealHeat,
    HeatTolerance,
    IdealLight,
    LightTolerance,
    ToxinTolerance,
    Pressure,
    PestTolerance,
    WeedTolerance,
    Subtypes,
    Mutations,
}

public static class PlantMutationCategories
{
    public static PlantMutationCategory? GetCategory(string mutationName)
    {
        return mutationName switch
        {
            "ChangeYield" => PlantMutationCategory.Yield,
            "ChangePotency" => PlantMutationCategory.Potency,
            "ChangeHarvest" => PlantMutationCategory.Harvest,
            "ChangeChemicals" => PlantMutationCategory.ContainedSubstances,
            "ChangeConsumeGasses" => PlantMutationCategory.ConsumedGas,
            "ChangeExudeGasses" => PlantMutationCategory.EmittedGas,
            "ChangeLifespan" => PlantMutationCategory.Lifespan,
            "ChangeMaturation" => PlantMutationCategory.Maturation,
            "ChangeProduction" => PlantMutationCategory.Production,
            "ChangeGrowthStages" => PlantMutationCategory.Stages,
            "ChangeEndurance" => PlantMutationCategory.Endurance,
            "ChangeNutrientConsumption" => PlantMutationCategory.NutrientUsage,
            "ChangeWaterConsumption" => PlantMutationCategory.WaterUsage,
            "ChangeIdealHeat" => PlantMutationCategory.IdealHeat,
            "ChangeHeatTolerance" => PlantMutationCategory.HeatTolerance,
            "ChangeIdealLight" => PlantMutationCategory.IdealLight,
            "ChangeLightTolerance" => PlantMutationCategory.LightTolerance,
            "ChangeToxinsTolerance" => PlantMutationCategory.ToxinTolerance,
            "ChangeLowPressureTolerance" => PlantMutationCategory.Pressure,
            "ChangeHighPressureTolerance" => PlantMutationCategory.Pressure,
            "ChangePestTolerance" => PlantMutationCategory.PestTolerance,
            "ChangeWeedTolerance" => PlantMutationCategory.WeedTolerance,
            "ChangeSpecies" => PlantMutationCategory.Subtypes,
            "Seedless" => PlantMutationCategory.Mutations,
            "ChangeSeedless" => PlantMutationCategory.Mutations,
            "Slippery" => PlantMutationCategory.Mutations,
            "Mandragora" => PlantMutationCategory.Mutations,
            "ChangeScreaming" => PlantMutationCategory.Mutations,
            "Ligneous" => PlantMutationCategory.Mutations,
            "Lignification" => PlantMutationCategory.Mutations,
            "Kudzufication" => PlantMutationCategory.Mutations,
            "Bioluminescent" => PlantMutationCategory.Mutations,
            "Bio-Luminescence" => PlantMutationCategory.Mutations,
            "Glow" => PlantMutationCategory.Mutations,
            "Unviable" => PlantMutationCategory.Mutations,
            _ => null,
        };
    }

    public static string GetIdentity(RandomPlantMutation mutation)
    {
        var category = GetCategory(mutation.Name);
        return category is null ? mutation.Name : $"category:{category.Value}";
    }
}

public partial class SeedData
{
    [DataField] public List<PlantMutationCategoryState> MutationCategoryStates = new();

    public void EnsureMutationCategoryState(PlantMutationCategory category)
    {
        if (TryGetMutationCategoryState(category) != null)
            return;

        MutationCategoryStates.Add(CaptureMutationCategoryState(category));
    }

    public void RestoreMutationCategoryState(PlantMutationCategory category)
    {
        var state = TryGetMutationCategoryState(category);
        if (state == null)
            return;

        switch (category)
        {
            case PlantMutationCategory.Yield:
            case PlantMutationCategory.Stages:
                SetMutationCategoryInt(category, state.IntValue);
                break;
            case PlantMutationCategory.Harvest:
                HarvestRepeat = state.HarvestValue;
                break;
            case PlantMutationCategory.ContainedSubstances:
            {
                var inherent = Chemicals.Where(pair => pair.Value.Inherent)
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                Chemicals = new Dictionary<string, SeedChemQuantity>(inherent);
                foreach (var (chemical, quantity) in state.Chemicals)
                {
                    Chemicals[chemical] = quantity;
                }
                break;
            }
            case PlantMutationCategory.ConsumedGas:
                ConsumeGasses = new Dictionary<Gas, float>(state.Gasses);
                break;
            case PlantMutationCategory.EmittedGas:
                ExudeGasses = new Dictionary<Gas, float>(state.Gasses);
                break;
            case PlantMutationCategory.Lifespan:
            case PlantMutationCategory.Maturation:
            case PlantMutationCategory.Production:
            case PlantMutationCategory.Endurance:
            case PlantMutationCategory.NutrientUsage:
            case PlantMutationCategory.WaterUsage:
            case PlantMutationCategory.IdealHeat:
            case PlantMutationCategory.HeatTolerance:
            case PlantMutationCategory.IdealLight:
            case PlantMutationCategory.LightTolerance:
            case PlantMutationCategory.ToxinTolerance:
            case PlantMutationCategory.PestTolerance:
            case PlantMutationCategory.WeedTolerance:
            case PlantMutationCategory.Potency:
                SetMutationCategoryFloat(category, state.FloatValue);
                break;
            case PlantMutationCategory.Pressure:
                LowPressureTolerance = state.FloatValue;
                HighPressureTolerance = state.SecondaryFloatValue;
                break;
            case PlantMutationCategory.Subtypes:
            {
                Name = state.Name;
                Noun = state.Noun;
                PacketName = state.PacketName;
                DisplayName = state.DisplayName;
                PacketPrototype = state.PacketPrototype;
                Mysterious = state.Mysterious;
                ProductPrototypes = new List<string>(state.ProductPrototypes);
                MutationPrototypes = new List<string>(state.MutationPrototypes);
                GrowthStages = state.IntValue;
                PlantRsi = state.PlantRsi;
                PlantIconState = state.PlantIconState;
                SplatPrototype = state.SplatPrototype;

                var nonInherent = Chemicals.Where(pair => !pair.Value.Inherent)
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                Chemicals = new Dictionary<string, SeedChemQuantity>(state.Chemicals);
                foreach (var (chemical, quantity) in nonInherent)
                {
                    Chemicals[chemical] = quantity;
                }
                break;
            }
            case PlantMutationCategory.Mutations:
                Seedless = state.BoolValue;
                Ligneous = state.SecondaryBoolValue;
                TurnIntoKudzu = state.TertiaryBoolValue;
                CanScream = state.QuaternaryBoolValue;
                Viable = state.QuinaryBoolValue;
                break;
        }

        Mutations.RemoveAll(mutation => PlantMutationCategories.GetCategory(mutation.Name) == category);
        Mutations.AddRange(state.MutationEntries);
    }

    private PlantMutationCategoryState CaptureMutationCategoryState(PlantMutationCategory category)
    {
        var state = new PlantMutationCategoryState
        {
            Category = category,
            MutationEntries = Mutations
                .Where(mutation => PlantMutationCategories.GetCategory(mutation.Name) == category)
                .ToList(),
        };

        switch (category)
        {
            case PlantMutationCategory.Yield:
                state.IntValue = Yield;
                break;
            case PlantMutationCategory.Potency:
                state.FloatValue = Potency;
                break;
            case PlantMutationCategory.Harvest:
                state.HarvestValue = HarvestRepeat;
                break;
            case PlantMutationCategory.ContainedSubstances:
                state.Chemicals = Chemicals.Where(pair => !pair.Value.Inherent)
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                break;
            case PlantMutationCategory.ConsumedGas:
                state.Gasses = new Dictionary<Gas, float>(ConsumeGasses);
                break;
            case PlantMutationCategory.EmittedGas:
                state.Gasses = new Dictionary<Gas, float>(ExudeGasses);
                break;
            case PlantMutationCategory.Lifespan:
                state.FloatValue = Lifespan;
                break;
            case PlantMutationCategory.Maturation:
                state.FloatValue = Maturation;
                break;
            case PlantMutationCategory.Production:
                state.FloatValue = Production;
                break;
            case PlantMutationCategory.Stages:
                state.IntValue = GrowthStages;
                break;
            case PlantMutationCategory.Endurance:
                state.FloatValue = Endurance;
                break;
            case PlantMutationCategory.NutrientUsage:
                state.FloatValue = NutrientConsumption;
                break;
            case PlantMutationCategory.WaterUsage:
                state.FloatValue = WaterConsumption;
                break;
            case PlantMutationCategory.IdealHeat:
                state.FloatValue = IdealHeat;
                break;
            case PlantMutationCategory.HeatTolerance:
                state.FloatValue = HeatTolerance;
                break;
            case PlantMutationCategory.IdealLight:
                state.FloatValue = IdealLight;
                break;
            case PlantMutationCategory.LightTolerance:
                state.FloatValue = LightTolerance;
                break;
            case PlantMutationCategory.ToxinTolerance:
                state.FloatValue = ToxinsTolerance;
                break;
            case PlantMutationCategory.Pressure:
                state.FloatValue = LowPressureTolerance;
                state.SecondaryFloatValue = HighPressureTolerance;
                break;
            case PlantMutationCategory.PestTolerance:
                state.FloatValue = PestTolerance;
                break;
            case PlantMutationCategory.WeedTolerance:
                state.FloatValue = WeedTolerance;
                break;
            case PlantMutationCategory.Subtypes:
                state.Name = Name;
                state.Noun = Noun;
                state.PacketName = PacketName;
                state.DisplayName = DisplayName;
                state.PacketPrototype = PacketPrototype;
                state.Mysterious = Mysterious;
                state.ProductPrototypes = new List<string>(ProductPrototypes);
                state.MutationPrototypes = new List<string>(MutationPrototypes);
                state.IntValue = GrowthStages;
                state.PlantRsi = PlantRsi;
                state.PlantIconState = PlantIconState;
                state.SplatPrototype = SplatPrototype;
                state.Chemicals = Chemicals.Where(pair => pair.Value.Inherent)
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                break;
            case PlantMutationCategory.Mutations:
                state.BoolValue = Seedless;
                state.SecondaryBoolValue = Ligneous;
                state.TertiaryBoolValue = TurnIntoKudzu;
                state.QuaternaryBoolValue = CanScream;
                state.QuinaryBoolValue = Viable;
                break;
        }

        return state;
    }

    private PlantMutationCategoryState? TryGetMutationCategoryState(PlantMutationCategory category)
    {
        return MutationCategoryStates.FirstOrDefault(state => state.Category == category);
    }

    private void SetMutationCategoryFloat(PlantMutationCategory category, float value)
    {
        switch (category)
        {
            case PlantMutationCategory.Potency:
                Potency = value;
                break;
            case PlantMutationCategory.Lifespan:
                Lifespan = value;
                break;
            case PlantMutationCategory.Maturation:
                Maturation = value;
                break;
            case PlantMutationCategory.Production:
                Production = value;
                break;
            case PlantMutationCategory.Endurance:
                Endurance = value;
                break;
            case PlantMutationCategory.NutrientUsage:
                NutrientConsumption = value;
                break;
            case PlantMutationCategory.WaterUsage:
                WaterConsumption = value;
                break;
            case PlantMutationCategory.IdealHeat:
                IdealHeat = value;
                break;
            case PlantMutationCategory.HeatTolerance:
                HeatTolerance = value;
                break;
            case PlantMutationCategory.IdealLight:
                IdealLight = value;
                break;
            case PlantMutationCategory.LightTolerance:
                LightTolerance = value;
                break;
            case PlantMutationCategory.ToxinTolerance:
                ToxinsTolerance = value;
                break;
            case PlantMutationCategory.PestTolerance:
                PestTolerance = value;
                break;
            case PlantMutationCategory.WeedTolerance:
                WeedTolerance = value;
                break;
        }
    }

    private void SetMutationCategoryInt(PlantMutationCategory category, int value)
    {
        switch (category)
        {
            case PlantMutationCategory.Yield:
                Yield = value;
                break;
            case PlantMutationCategory.Stages:
                GrowthStages = value;
                break;
        }
    }
}