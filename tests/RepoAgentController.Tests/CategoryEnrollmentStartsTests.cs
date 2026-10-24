using Xunit;

namespace RepoAgentController.Tests;

// CategoryEnrollmentStarts in-process: how the recorded category enrollment starts follow the effective
// REPOAGENT_ENROLLMENT_CATEGORIES setting.
public sealed class CategoryEnrollmentStartsTests
{
    static readonly DateTimeOffset Observed = new(2026, 10, 24, 10, 0, 0, 750, TimeSpan.Zero);
    static readonly DateTimeOffset Second = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);

    static SettingValue Selected(string? value, SettingState state = SettingState.Configured) => new(state, value) { Since = Observed };

    [Fact]
    public void Newly_selected_categories_start_in_the_whole_second_the_setting_value_was_first_observed() =>
        Changes([], ["Agents", "Q&A"], CategoryEnrollmentStarts.Reconcile([], Selected("Agents, Q&A")));

    [Fact]
    public void A_removed_category_is_forgotten_while_the_others_keep_their_starts() =>
        Changes(["Agents"], [], CategoryEnrollmentStarts.Reconcile(["Agents", "Research"], Selected("Research")));

    [Fact]
    public void A_category_selected_again_starts_afresh_at_the_new_value() =>
        Changes([], ["Agents"], CategoryEnrollmentStarts.Reconcile(["Research"], Selected("Research, Agents")));

    [Fact]
    public void Names_compare_case_insensitively_so_a_changed_case_keeps_the_start() =>
        Assert.Null(CategoryEnrollmentStarts.Reconcile(["Agents", "Q&A"], Selected("q&a, AGENTS")));

    [Theory]
    [InlineData(SettingState.ReadFailed)]
    [InlineData(SettingState.Invalid)]
    [InlineData(SettingState.Pending)]
    public void Without_a_known_value_an_unusable_or_pending_read_changes_nothing(SettingState state) =>
        Assert.Null(CategoryEnrollmentStarts.Reconcile(["Agents"], Selected(null, state)));

    [Theory]
    [InlineData(SettingState.ReadFailed)]
    [InlineData(SettingState.Invalid)]
    public void A_retained_value_keeps_its_starts(SettingState state) =>
        Assert.Null(CategoryEnrollmentStarts.Reconcile(["Agents"], Selected("Agents", state)));

    [Theory]
    [InlineData(SettingState.Absent)]
    [InlineData(SettingState.Disabled)]
    public void Confirmed_absence_or_disabled_settings_forget_every_start(SettingState state)
    {
        Changes(["Agents", "Q&A"], [], CategoryEnrollmentStarts.Reconcile(["Agents", "Q&A"], Selected(null, state)));
        Assert.Null(CategoryEnrollmentStarts.Reconcile([], Selected(null, state)));
    }

    static void Changes(string[] forgotten, string[] started, CategoryStartChanges? changes)
    {
        Assert.NotNull(changes);
        Assert.Equal(forgotten, changes.Forgotten);
        Assert.Equal(started, changes.Started);
        Assert.Equal(Second, changes.Since);
    }
}
