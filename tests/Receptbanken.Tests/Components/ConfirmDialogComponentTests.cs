using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Receptbanken.Web.Components.Common;

namespace Receptbanken.Tests.Components;

public class ConfirmDialogComponentTests : TestContext
{
    [Fact]
    public void ConfirmDialog_WhenNotVisible_ShouldRenderNothing()
    {
        var cut = RenderComponent<ConfirmDialog>(parameters => parameters
            .Add(p => p.IsVisible, false));

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void ConfirmDialog_DefaultVariant_ShouldUseDangerCustomButton()
    {
        var cut = RenderComponent<ConfirmDialog>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.Title, "Radera recept")
            .Add(p => p.ConfirmText, "Ja, radera"));

        var confirmBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Ja, radera"));
        confirmBtn.ClassList.Should().Contain("btn-danger-custom");
        confirmBtn.ClassList.Should().Contain("btn-md");
        confirmBtn.ClassList.Should().NotContain("btn-primary-custom");
        (confirmBtn.GetAttribute("style") ?? string.Empty).Should().NotContain("padding");

        var cancelBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Avbryt"));
        cancelBtn.ClassList.Should().Contain("btn-secondary-custom");
        cancelBtn.ClassList.Should().Contain("btn-md");
    }

    [Fact]
    public void ConfirmDialog_WhenExplicitlySetToPrimary_ShouldUsePrimaryCustomButton()
    {
        var cut = RenderComponent<ConfirmDialog>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.Title, "Spara ändringar i veckomenyn?")
            .Add(p => p.ConfirmText, "Spara och fortsätt")
            .Add(p => p.Variant, ConfirmDialogVariant.Primary));

        var confirmBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Spara och fortsätt"));
        confirmBtn.ClassList.Should().Contain("btn-primary-custom");
        confirmBtn.ClassList.Should().NotContain("btn-danger-custom");

        var cancelBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Avbryt"));
        cancelBtn.ClassList.Should().Contain("btn-secondary-custom");
    }

    [Fact]
    public void ConfirmDialog_WhenExplicitlySetToDestructiveOrDanger_ShouldUseDangerCustomButton()
    {
        var cutDanger = RenderComponent<ConfirmDialog>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.Variant, ConfirmDialogVariant.Danger));

        var dangerBtn = cutDanger.FindAll("button").First(b => b.TextContent.Contains("Radera"));
        dangerBtn.ClassList.Should().Contain("btn-danger-custom");

        var cutDestructive = RenderComponent<ConfirmDialog>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.Variant, ConfirmDialogVariant.Destructive));

        var destructiveBtn = cutDestructive.FindAll("button").First(b => b.TextContent.Contains("Radera"));
        destructiveBtn.ClassList.Should().Contain("btn-danger-custom");
    }

    [Fact]
    public void ConfirmDialog_WhenConfirmClicked_ShouldInvokeOnConfirm()
    {
        bool confirmed = false;
        var cut = RenderComponent<ConfirmDialog>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.ConfirmText, "Spara och fortsätt")
            .Add(p => p.Variant, ConfirmDialogVariant.Primary)
            .Add(p => p.OnConfirm, EventCallback.Factory.Create(this, () => confirmed = true)));

        cut.Find("button.btn-primary-custom").Click();

        confirmed.Should().BeTrue();
    }

    [Fact]
    public void ConfirmDialog_WhenCancelClicked_ShouldInvokeOnCancel()
    {
        bool cancelled = false;
        var cut = RenderComponent<ConfirmDialog>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.OnCancel, EventCallback.Factory.Create(this, () => cancelled = true)));

        cut.Find("button.btn-secondary-custom").Click();

        cancelled.Should().BeTrue();
    }

    [Fact]
    public void ConfirmDialog_WhenBackdropClicked_ShouldInvokeOnCancel()
    {
        bool cancelled = false;
        var cut = RenderComponent<ConfirmDialog>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.OnCancel, EventCallback.Factory.Create(this, () => cancelled = true)));

        cut.Find(".modal-backdrop-custom").Click();

        cancelled.Should().BeTrue();
    }
}
