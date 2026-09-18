namespace BPM.Infrastructure.Services;

// Phase 7.1 — a single configuration value, deliberately not a bigger settings system (Part 14:
// "不要為一個 KPI 建立過度複雜的設定系統"). Mirrors EmailSettings/SlaSchedulerSettings' own shape
// (a plain options class bound from a named appsettings.json section).
public class DashboardSettings
{
    public const string SectionName = "Dashboard";

    // "Due Soon" = an Active TaskSla whose DueAt falls within this many hours from now, and has
    // not yet passed (a task past DueAt is already counted as Overdue instead — the two buckets
    // are mutually exclusive, see DashboardQueryService). No existing product spec defines this
    // threshold, so a conservative, clearly-documented default was chosen rather than inventing a
    // more elaborate configuration surface for one KPI.
    public int DueSoonHours { get; set; } = 24;
}
