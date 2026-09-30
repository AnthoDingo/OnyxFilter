using Microsoft.AspNetCore.Components;

namespace OnyxFilter.Components.Shared;

public partial class BrandMark : ComponentBase
{
    [Parameter]
    public string? Class { get; set; }
}
