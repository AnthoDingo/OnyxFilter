using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;

namespace OnyxFilter.Components.Shared;

public partial class DashboardPanel : ComponentBase
{
    [Parameter]
    public string Title { get; set; } = string.Empty;

    [Parameter]
    public string Subtitle { get; set; } = string.Empty;

    [Parameter]
    public EventCallback OnRefresh { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    private Task HandleRefreshAsync()
    {
        return OnRefresh.InvokeAsync();
    }
}
