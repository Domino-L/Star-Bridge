using System.Windows;
using System.Windows.Input;

namespace StarBridge.Desktop;

internal enum OnboardingNextAction
{
    None,
    Login,
    SelectLog,
    FindFleet,
    CreateFleet,
    MySquad,
    Overlay
}

public partial class OnboardingWindow : Window
{
    private bool _markCompleted;

    internal OnboardingNextAction NextAction { get; private set; } = OnboardingNextAction.None;

    internal bool ShouldMarkCompleted => _markCompleted;

    public OnboardingWindow(
        bool isLoggedIn,
        bool hasLog,
        bool hasFleet,
        bool hasSquad,
        bool hasOverlayLayout)
    {
        InitializeComponent();

        AccountStateText.Text = isLoggedIn ? "已登录" : "未登录";
        LogStateText.Text = hasLog ? "已选择" : "未选择";
        FleetStateText.Text = hasFleet ? "已加入舰队" : "未加入舰队";
        SquadStateText.Text = hasSquad ? "已加入小队" : "未加入小队";
        OverlayStateText.Text = hasOverlayLayout ? "已配置布局" : "可稍后配置";
    }

    private void CompleteWithAction(OnboardingNextAction action)
    {
        NextAction = action;
        _markCompleted = CompleteAfterActionCheck.IsChecked == true;
        DialogResult = true;
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e) => CompleteWithAction(OnboardingNextAction.Login);

    private void SelectLogButton_Click(object sender, RoutedEventArgs e) => CompleteWithAction(OnboardingNextAction.SelectLog);

    private void FindFleetButton_Click(object sender, RoutedEventArgs e) => CompleteWithAction(OnboardingNextAction.FindFleet);

    private void CreateFleetButton_Click(object sender, RoutedEventArgs e) => CompleteWithAction(OnboardingNextAction.CreateFleet);

    private void MySquadButton_Click(object sender, RoutedEventArgs e) => CompleteWithAction(OnboardingNextAction.MySquad);

    private void OverlayButton_Click(object sender, RoutedEventArgs e) => CompleteWithAction(OnboardingNextAction.Overlay);

    private void FinishButton_Click(object sender, RoutedEventArgs e)
    {
        NextAction = OnboardingNextAction.None;
        _markCompleted = true;
        DialogResult = true;
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        NextAction = OnboardingNextAction.None;
        _markCompleted = false;
        DialogResult = false;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
