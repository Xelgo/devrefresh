namespace DevRefresh;

public sealed partial class Store
{
    // Resolve permissions again when a queued/scheduled shared-contour refresh starts.
    public bool CanRefreshTestContour(string actor, IConfiguration configuration)
    {
        if (GetAdmin()?.Username == actor) return true;
        var access = GetWindowsAccess(configuration);
        return access.Enabled && (GetWindowsAdmins().Contains(actor, StringComparer.OrdinalIgnoreCase)
            || (access.TestContourOperators ?? []).Contains(actor, StringComparer.OrdinalIgnoreCase));
    }
}
