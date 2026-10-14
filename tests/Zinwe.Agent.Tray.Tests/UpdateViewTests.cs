using Zinwe.Agent.Core.Ipc;
using Zinwe.Agent.Tray.Widget;

namespace Zinwe.Agent.Tray.Tests;

public sealed class UpdateViewTests
{
    private static AgentStatus Status(AgentState state, string? version = "0.3.0", string? stage = null,
        string? message = null, bool installable = true, string? notes = "Faster chat") =>
        new(state, "0.2.0", "pc", 1, null, null,
            UpdateVersion: version, UpdateNotes: notes, UpdateStage: stage, UpdateMessage: message, UpdateInstallable: installable);

    [Fact]
    public void Up_to_date_shows_nothing()
    {
        Assert.False(UpdateView.From(Status(AgentState.Connected, version: null)).Show);
        Assert.False(UpdateView.From(null).Show);
    }

    [Fact]
    public void A_required_update_says_work_is_paused_and_offers_one_click()
    {
        var view = UpdateView.From(Status(AgentState.UpdateRequired));
        Assert.True(view.Show && view.Required);
        Assert.Equal("Update Zinwe to keep working", view.Title);
        Assert.Contains("Zinwe 0.3.0 is required", view.Body);
        Assert.Contains("paused", view.Body);
        Assert.Equal(("Update now", true, true), (view.Button, view.ButtonVisible, view.ButtonEnabled));
        Assert.Equal("Faster chat", view.Notes);
        Assert.False(view.ShowManual);
    }

    [Fact]
    public void An_optional_update_does_not_pause_anything()
    {
        var view = UpdateView.From(Status(AgentState.Connected));
        Assert.True(view.Show);
        Assert.False(view.Required);
        Assert.Equal("Update available", view.Title);
        Assert.DoesNotContain("paused", view.Body);
    }

    [Theory]
    [InlineData(UpdateStages.Downloading, "Downloading…", true)]
    [InlineData(UpdateStages.Installing, "Installing…", true)]
    [InlineData(UpdateStages.Ready, "Downloaded", false)]
    public void Progress_disables_the_button_and_shows_the_stage(string stage, string button, bool busy)
    {
        var view = UpdateView.From(Status(AgentState.UpdateRequired, stage: stage, message: "msg"));
        Assert.Equal((button, false, busy, "msg"), (view.Button, view.ButtonEnabled, view.Busy, view.Progress));
    }

    [Fact]
    public void A_failure_offers_try_again_and_the_manual_download()
    {
        var view = UpdateView.From(Status(AgentState.UpdateRequired, stage: UpdateStages.Failed, message: "safety check"));
        Assert.Equal(("Try again", true, true), (view.Button, view.ButtonEnabled, view.ShowManual));
        Assert.True(view.ProgressIsError);
        Assert.Equal("safety check", view.Progress);
    }

    [Fact]
    public void Without_a_published_installer_only_the_manual_download_is_offered()
    {
        // e.g. only the env minimum version was raised, no MSI uploaded
        var view = UpdateView.From(Status(AgentState.UpdateRequired, installable: false));
        Assert.False(view.ButtonVisible);
        Assert.True(view.ShowManual);
    }
}
