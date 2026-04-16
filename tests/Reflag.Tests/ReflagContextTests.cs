using Reflag;
using Xunit;

namespace Reflag.Tests;

public sealed class ReflagContextTests
{
    [Fact]
    public void ReflagContext_From_maps_known_fields_and_custom_attributes_from_anonymous_object()
    {
        var context = ReflagContext.From(new
        {
            User = new
            {
                Id = "user-123",
                Name = "Ada",
                Email = "ada@example.com",
                Plan = "enterprise",
            },
            Company = new
            {
                Id = "company-456",
                Name = "Acme",
                Tier = "gold",
            },
            Other = new
            {
                Device = "Desktop",
            },
        });

        Assert.Equal("user-123", context.User?.Id);
        Assert.Equal("Ada", context.User?.Name);
        Assert.Equal("ada@example.com", context.User?.Email);
        Assert.Equal("enterprise", context.User?.Attributes["Plan"]);
        Assert.Equal("company-456", context.Company?.Id);
        Assert.Equal("Acme", context.Company?.Name);
        Assert.Equal("gold", context.Company?.Attributes["Tier"]);
        Assert.Equal("Desktop", context.Other?["Device"]);
    }

    [Fact]
    public void ReflagContext_From_supports_nested_dictionary_authoring()
    {
        var context = ReflagContext.From(new Dictionary<string, object?>
        {
            ["User"] = new Dictionary<string, object?>
            {
                ["Id"] = "user-123",
                ["Name"] = "Ada",
                ["Plan"] = "enterprise",
            },
            ["Company"] = new Dictionary<string, object?>
            {
                ["Id"] = "company-456",
                ["Name"] = "Acme",
            },
            ["Other"] = new Dictionary<string, object?>
            {
                ["Device"] = "Desktop",
            },
        });

        Assert.Equal("user-123", context.User?.Id);
        Assert.Equal("Ada", context.User?.Name);
        Assert.Equal("enterprise", context.User?.Attributes["Plan"]);
        Assert.Equal("company-456", context.Company?.Id);
        Assert.Equal("Acme", context.Company?.Name);
        Assert.Equal("Desktop", context.Other?["Device"]);
    }
}
