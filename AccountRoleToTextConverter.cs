using System.Globalization;
using System.Windows.Data;
using RoomTalk.Models;

namespace RoomTalk;

public sealed class AccountRoleToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is AccountRole role
            ? role switch
            {
                AccountRole.Server => "Máy chủ",
                AccountRole.Admin => "Điều hành",
                _ => "Người dùng"
            }
            : string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
