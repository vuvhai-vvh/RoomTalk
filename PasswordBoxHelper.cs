using System.Windows;
using System.Windows.Controls;

namespace RoomTalk;

public static class PasswordBoxHelper
{
    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword",
            typeof(string),
            typeof(PasswordBoxHelper),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBoundPasswordChanged));

    public static readonly DependencyProperty BindPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BindPassword",
            typeof(bool),
            typeof(PasswordBoxHelper),
            new PropertyMetadata(false, OnBindPasswordChanged));

    private static readonly DependencyProperty UpdatingPasswordProperty =
        DependencyProperty.RegisterAttached(
            "UpdatingPassword",
            typeof(bool),
            typeof(PasswordBoxHelper),
            new PropertyMetadata(false));

    public static string GetBoundPassword(DependencyObject obj) =>
        (string)obj.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject obj, string value) =>
        obj.SetValue(BoundPasswordProperty, value);

    public static bool GetBindPassword(DependencyObject obj) =>
        (bool)obj.GetValue(BindPasswordProperty);

    public static void SetBindPassword(DependencyObject obj, bool value) =>
        obj.SetValue(BindPasswordProperty, value);

    private static bool GetUpdatingPassword(DependencyObject obj) =>
        (bool)obj.GetValue(UpdatingPasswordProperty);

    private static void SetUpdatingPassword(DependencyObject obj, bool value) =>
        obj.SetValue(UpdatingPasswordProperty, value);

    private static void OnBindPasswordChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not PasswordBox passwordBox)
        {
            return;
        }

        passwordBox.PasswordChanged -= PasswordBoxOnPasswordChanged;

        if ((bool)args.NewValue)
        {
            passwordBox.PasswordChanged += PasswordBoxOnPasswordChanged;
        }
    }

    private static void OnBoundPasswordChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not PasswordBox passwordBox || GetUpdatingPassword(passwordBox))
        {
            return;
        }

        passwordBox.Password = args.NewValue?.ToString() ?? string.Empty;
    }

    private static void PasswordBoxOnPasswordChanged(object sender, RoutedEventArgs args)
    {
        if (sender is not PasswordBox passwordBox)
        {
            return;
        }

        SetUpdatingPassword(passwordBox, true);
        SetBoundPassword(passwordBox, passwordBox.Password);
        SetUpdatingPassword(passwordBox, false);
    }
}
