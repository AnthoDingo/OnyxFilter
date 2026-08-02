using System.Collections.Generic;
using Microsoft.AspNetCore.Components;

namespace OnyxFilter.Components.Account.Shared;

public partial class PasskeySubmit : ComponentBase
{
    [Parameter]
    [EditorRequired]
    public PasskeyOperation Operation { get; set; }

    [Parameter]
    [EditorRequired]
    public string Name { get; set; } = string.Empty;

    [Parameter]
    public string? EmailName { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    [Parameter(CaptureUnmatchedValues = true)]
    public IDictionary<string, object>? AdditionalAttributes { get; set; }
}
