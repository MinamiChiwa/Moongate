using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace MabiTool.ObservationDebugTool;

/// <summary>
/// 置顶的本机操作面板。窗口只读取会话快照并转发按钮；采集、计时和资源释放全部由 DebugSession 负责。
/// 运行中关闭窗口等同于停止，资源释放与报告完成后窗口才关闭。
/// </summary>
public sealed class DebugPanel : IDisposable
{
    private readonly DebugSession session;
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? thread;
    private PanelForm? form;

    public DebugPanel(DebugSession session) => this.session = session;
    public Task Closed => closed.Task;

    public Task ShowAsync()
    {
        thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                form = new PanelForm(session, () => shown.TrySetResult());
                Application.Run(form);
                closed.TrySetResult();
            }
            catch (Exception error)
            {
                // 面板故障时停止当前场景，由会话完成释放和报告。
                session.RequestExit();
                shown.TrySetException(error); closed.TrySetException(error);
            }
        }) { IsBackground = true, Name = "ObservationDebugTool.Panel" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return shown.Task;
    }

    public void Dispose()
    {
        var value = form;
        if (value is not null && !value.IsDisposed && value.IsHandleCreated)
            try { value.BeginInvoke(() => value.CloseFromOwner()); } catch (InvalidOperationException) { }
    }

    private sealed class PanelForm : Form
    {
        private readonly DebugSession session;
        private readonly Action onShown;
        private readonly ListBox scenarios = new();
        private readonly TextBox plan = new();
        private readonly Label stage = new();
        private readonly Label instruction = new();
        private readonly Label remaining = new();
        private readonly Label next = new();
        private readonly TextBox results = new();
        private readonly Button start = new(), action = new(), stop = new(), exit = new();
        private readonly FlowLayoutPanel answers = new();
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 200 };
        private string? renderedPlan, renderedResults, renderedQuestion, renderedList;
        private bool closing, ownerClose;

        public PanelForm(DebugSession session, Action onShown)
        {
            this.session = session; this.onShown = onShown;
            Text = "Moongate 实机调试";
            ClientSize = new Size(900, 640);
            MinimumSize = new Size(760, 560);
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(28, 31, 36);
            ForeColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 10);

            scenarios.SetBounds(16, 16, 280, 300);
            scenarios.BackColor = Color.FromArgb(40, 44, 52); scenarios.ForeColor = Color.White;
            scenarios.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            scenarios.SelectedIndexChanged += (_, _) =>
            {
                if (scenarios.SelectedItem is Item item && !session.Select(item.Id)) renderedList = null;
            };

            plan.SetBounds(312, 16, 572, 300);
            plan.Multiline = true; plan.ReadOnly = true; plan.ScrollBars = ScrollBars.Vertical;
            plan.BackColor = Color.FromArgb(40, 44, 52); plan.ForeColor = Color.White;
            plan.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            stage.Font = new Font("Microsoft YaHei UI", 15, FontStyle.Bold);
            stage.SetBounds(16, 326, 600, 32); stage.Text = "选择场景后点击“开始”";
            remaining.Font = new Font("Consolas", 20, FontStyle.Bold);
            remaining.ForeColor = Color.FromArgb(112, 209, 255);
            remaining.SetBounds(620, 322, 264, 40); remaining.TextAlign = ContentAlignment.MiddleRight;
            remaining.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            instruction.SetBounds(16, 362, 868, 52); instruction.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            next.SetBounds(16, 414, 868, 24); next.ForeColor = Color.FromArgb(190, 197, 208);
            next.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            answers.SetBounds(16, 440, 868, 40); answers.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            Style(start, "开始", Color.FromArgb(30, 130, 190)); start.SetBounds(16, 486, 150, 44);
            Style(action, "继续", Color.FromArgb(46, 140, 90)); action.SetBounds(176, 486, 300, 44);
            Style(stop, "停止", Color.FromArgb(156, 64, 64)); stop.SetBounds(486, 486, 150, 44);
            Style(exit, "退出", Color.FromArgb(80, 86, 96)); exit.SetBounds(646, 486, 150, 44);
            start.Click += (_, _) => session.Start();
            action.Click += (_, _) => session.Act();
            stop.Click += (_, _) => session.Stop();
            exit.Click += (_, _) => { session.RequestExit(); };

            results.SetBounds(16, 538, 868, 86);
            results.Multiline = true; results.ReadOnly = true; results.ScrollBars = ScrollBars.Vertical;
            results.BackColor = Color.FromArgb(40, 44, 52); results.ForeColor = Color.White;
            results.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            Controls.AddRange([scenarios, plan, stage, remaining, instruction, next, answers, start, action, stop, exit, results]);
            Shown += (_, _) => { timer.Start(); Render(); onShown(); };
            FormClosing += OnClosing;
            FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); };
            timer.Tick += (_, _) => Render();
        }

        protected override bool ShowWithoutActivation => true;

        private static void Style(Button button, string text, Color color)
        {
            button.Text = text; button.FlatStyle = FlatStyle.Flat; button.BackColor = color; button.ForeColor = Color.White;
            button.Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold);
        }

        public void CloseFromOwner() { ownerClose = true; Close(); }

        private void OnClosing(object? sender, FormClosingEventArgs e)
        {
            if (ownerClose || session.State == DebugSession.Exited) return;
            e.Cancel = true; closing = true;
            session.RequestExit();
        }

        private void Render()
        {
            var value = session.Snapshot();
            if (value.State == DebugSession.Exited) { ownerClose = true; Close(); return; }
            var list = string.Join("|", value.Scenarios.Select(item => item.Id + item.Status)) + value.SelectedId;
            if (list != renderedList)
            {
                renderedList = list;
                scenarios.BeginUpdate(); scenarios.Items.Clear();
                foreach (var item in value.Scenarios) scenarios.Items.Add(new Item(item.Id, $"[{item.Status}] {item.Title}"));
                scenarios.SelectedIndex = value.Scenarios.ToList().FindIndex(item => item.Id == value.SelectedId);
                scenarios.EndUpdate();
            }
            scenarios.Enabled = value.State == DebugSession.Idle;
            var planText = PlanText(value.SelectedPlan, value.Preview);
            if (planText != renderedPlan) { renderedPlan = planText; plan.Text = planText; }
            if (value.Display is { } display)
            {
                stage.Text = display.Stage; instruction.Text = display.Instruction; next.Text = display.Next ?? "";
                remaining.Text = display.RemainingSeconds is { } seconds ? Format(seconds) : "";
            }
            else
            {
                stage.Text = closing ? "正在退出" : value.Preview ? "预览模式：不会启动网络观察" : "选择场景后点击“开始”";
                instruction.Text = value.Results.LastOrDefault() is { } last ? $"上一场景：{last.Title}——{last.Summary}" : "";
                next.Text = ""; remaining.Text = "";
            }
            start.Enabled = value.CanStart; stop.Enabled = value.CanStop;
            exit.Enabled = value.CanExit && !closing;
            action.Visible = value.ActionLabel is not null; action.Text = value.ActionLabel ?? ""; action.Enabled = value.ActionEnabled;
            var question = value.Question is null ? null : value.Question + "\u0001" + string.Join("|", value.Options);
            if (question != renderedQuestion)
            {
                renderedQuestion = question;
                answers.Controls.Clear();
                if (value.Question is not null)
                {
                    stage.Text = value.Question;
                    if (value.QuestionDetail is not null) { plan.Text = value.QuestionDetail; renderedPlan = null; }
                    foreach (var option in value.Options)
                    {
                        var button = new Button { Width = 150, Height = 34 };
                        Style(button, option, Color.FromArgb(70, 110, 160));
                        button.Click += (_, _) => session.Answer(option);
                        answers.Controls.Add(button);
                    }
                }
            }
            if (value.Question is not null && value.QuestionDetail is not null && plan.Text != value.QuestionDetail) plan.Text = value.QuestionDetail;
            var resultText = string.Join(Environment.NewLine, value.Results.Select(item =>
                $"{item.Title}：{Outcome(item.Outcome)}{(item.Accepted ? "（通过）" : "")}。{item.Summary}。报告：{item.ReportPath ?? "写入失败 " + item.ReportError}"));
            if (resultText != renderedResults) { renderedResults = resultText; results.Text = resultText; }
        }

        private static string Outcome(string value) => value switch
        {
            ScenarioResult.Completed => "已完成", ScenarioResult.Cancelled => "已停止", ScenarioResult.TimedOut => "已超时", _ => "失败"
        };

        private static string PlanText(ScenarioPlan? value, bool preview)
        {
            if (value is null) return "";
            var text = new StringBuilder();
            text.Append(value.Title).AppendLine().Append("关联任务：").Append(value.Tasks).AppendLine();
            text.Append("开始时点：点击“开始”后立即开始观察网络").AppendLine();
            text.Append("准备上限：").Append(Format(value.PreparationLimitSeconds)).Append("；采集时长：").Append(Format(value.RunSeconds)).AppendLine();
            if (value.Unavailable is not null) text.Append("当前不可执行：").Append(value.Unavailable).AppendLine();
            if (preview) text.Append("预览模式：不会建立 Host、枚举网卡或观察网络。").AppendLine();
            text.AppendLine().Append("需要在游戏内完成的动作：").AppendLine();
            var index = 1;
            foreach (var step in value.OperatorActions) text.Append(index++.ToString(CultureInfo.InvariantCulture)).Append(". ").Append(step).AppendLine();
            return text.ToString();
        }

        private static string Format(double seconds)
        {
            var whole = Math.Max(0, (long)Math.Ceiling(seconds));
            return $"{whole / 60:00}:{whole % 60:00}";
        }

        private sealed record Item(string Id, string Text) { public override string ToString() => Text; }
    }
}
