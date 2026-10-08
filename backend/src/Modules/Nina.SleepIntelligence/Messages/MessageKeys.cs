namespace Nina.SleepIntelligence;

/// <summary>Chaves de mensagem emitidas pelo motor (<c>explanation.key</c>). Os textos são localizados pelos clientes.</summary>
public static class MessageKeys
{
    public const string Disclaimer = SleepEngineInfo.DisclaimerKey;
    public const string AgeOnly = "prediction.explain.age_only";
    public const string HistoryAndAge = "prediction.explain.history_and_age";
    public const string IrregularHistory = "prediction.explain.irregular_history";
    public const string BedtimeAgeOnly = "prediction.explain.bedtime_age_only";
    public const string BedtimeHistoryAndAge = "prediction.explain.bedtime_history_and_age";
    public const string BedtimePreference = "prediction.explain.bedtime_preference";
    public const string AgeOutOfRange = "prediction.explain.age_out_of_range";
    public const string TimezoneUnknown = "prediction.explain.timezone_unknown";
    public const string NapTargetReached = "prediction.explain.nap_target_reached";
    public const string SleepingNow = "prediction.explain.sleeping_now";

    public static IReadOnlyList<string> All { get; } =
    [
        Disclaimer, AgeOnly, HistoryAndAge, IrregularHistory, BedtimeAgeOnly, BedtimeHistoryAndAge,
        BedtimePreference, AgeOutOfRange, TimezoneUnknown, NapTargetReached, SleepingNow,
    ];
}
