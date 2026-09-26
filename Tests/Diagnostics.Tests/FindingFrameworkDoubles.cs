// Only the framework plumbing is substituted. Tests exercise the production finding view model.
namespace CommunityToolkit.Mvvm.ComponentModel
{
    public abstract class ObservableObject
    {
        protected bool SetProperty<T>(ref T field, T value)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            return true;
        }
    }
}

namespace Microsoft.UI.Xaml
{
    public enum Visibility
    {
        Visible,
        Collapsed,
    }
}
