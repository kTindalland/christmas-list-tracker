using ChristmasListTracker.Models;

namespace ChristmasListTracker.Services;

public sealed class CurrentUserState
{
    public FamilyUser? User { get; private set; }

    public event Action? Changed;

    public void SetUser(FamilyUser? user)
    {
        User = user;
        Changed?.Invoke();
    }
}
