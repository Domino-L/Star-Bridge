using System;
using System.Threading.Tasks;
using System.Windows;

namespace StarBridge.Desktop;

public partial class LoginWindow : Window
{
    public Func<string, Task<string>>? SendVerificationCodeAsync { get; set; }

    public LoginWindow(string? loginEmail)
    {
        InitializeComponent();
        LoginEmailBox.Text = loginEmail ?? "";
        RegisterEmailBox.Text = loginEmail ?? "";
        SetMode(isRegister: false);
        LoginEmailBox.Focus();
    }

    public bool IsRegisterMode { get; private set; }

    public bool IsSkipped { get; private set; }

    public string LoginEmail => LoginEmailBox.Text.Trim();

    public string LoginPassword => LoginPasswordBox.Password;

    public string RegisterEmail => RegisterEmailBox.Text.Trim();

    public string RegisterPassword => RegisterPasswordBox.Password;

    public string RegisterCallsign => RegisterCallsignBox.Text.Trim();

    public string RegisterVerificationCode => VerificationCodeBox.Text.Trim();

    private void LoginModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetMode(isRegister: false);
    }

    private void RegisterModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetMode(isRegister: true);
    }

    private void SetMode(bool isRegister)
    {
        IsRegisterMode = isRegister;
        LoginPanel.Visibility = isRegister ? Visibility.Collapsed : Visibility.Visible;
        RegisterPanel.Visibility = isRegister ? Visibility.Visible : Visibility.Collapsed;
        LoginModeButton.Style = (Style)FindResource(isRegister ? "SecondaryButton" : "PrimaryButton");
        RegisterModeButton.Style = (Style)FindResource(isRegister ? "PrimaryButton" : "SecondaryButton");
        ConfirmButton.Content = isRegister ? "注册" : "登录";
        HintText.Text = isRegister
            ? "注册后将使用登录邮箱接收验证码，并把呼号绑定到你的星海舰桥个人身份。"
            : "使用注册邮箱登录。未登录时只能浏览，无法同步和管理舰队。";
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private async void SendCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (SendVerificationCodeAsync is null)
        {
            HintText.Text = "当前未连接验证码服务。";
            return;
        }

        SendCodeButton.IsEnabled = false;
        try
        {
            HintText.Text = await SendVerificationCodeAsync(RegisterEmail);
        }
        finally
        {
            SendCodeButton.IsEnabled = true;
        }
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        IsSkipped = true;
        DialogResult = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
