using System;
using Microsoft.AspNetCore.Components;

namespace OnyxFilter.Components.Shared;

public partial class StatusMessage : ComponentBase
{
    [Parameter]
    public string? Message { get; set; }

    private string StatusMessageClass => Message is not null && Message.StartsWith("Erreur", StringComparison.Ordinal)
        ? "danger"
        : "success";
}
