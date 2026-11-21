using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DSRMeteorPilot;

public sealed partial class Plugin
{
    private bool uiOpen;
    private bool dependencyRefreshPending;
    private string dependencyStatus = "導入状態を確認中…";
    private string dependencyAdvice = "";
    private static void Section(string title)
    {
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.55f, 0.8f, 1f, 1f), title);
    }
    private void OpenUi()
    {
        if (!uiOpen) dependencyRefreshPending = true;
        uiOpen = true;
    }

    private void DrawDependencies()
    {
        if (dependencyRefreshPending)
        {
            dependencyRefreshPending = false;
            try
            {
                var installed = Pi.InstalledPlugins.Where(p =>
                    string.Equals(p.InternalName, "vnavmesh", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (installed.Length == 0)
                {
                    dependencyStatus = "未インストール";
                    dependencyAdvice = "自動移動にはvnavmeshが必要です。vnavmeshをインストールして有効化してください。";
                }
                else if (!installed.Any(p => p.IsLoaded))
                {
                    dependencyStatus = "◯ インストール済み・無効";
                    dependencyAdvice = "vnavmeshを有効化してください。読み込みに失敗している場合は、プラグイン一覧で状態を確認してください。";
                }
                else
                {
                    dependencyStatus = "◯ インストール済み・有効";
                    try
                    {
                        dependencyAdvice = ready.InvokeFunc()
                            ? "自動移動の準備ができています。"
                            : "現在のエリアの移動データは準備未完了です。読み込みを待ち、vnavmeshの状態を確認してください。";
                    }
                    catch
                    {
                        dependencyAdvice = "vnavmeshとの連携を確認できません。バージョンや読み込み状態を確認してください。";
                    }
                }
            }
            catch
            {
                dependencyStatus = "確認できません";
                dependencyAdvice = "プラグイン一覧でvnavmeshの導入状態を確認してください。";
            }
        }
        Section("必要なプラグイン");
        ImGui.TextWrapped("vnavmesh（自動移動用）: " + dependencyStatus);
        ImGui.TextWrapped(dependencyAdvice);
        ImGui.TextDisabled("状態はこの画面を開いたときに更新されます。");
    }

    private void DrawUi()
    {
        if (!uiOpen) return;
        ImGui.SetNextWindowSize(new Vector2(570, 570), ImGuiCond.FirstUseEver);
        var visible = ImGui.Begin("DSR Meteor Pilot###DSRMeteorPilot", ref uiOpen);
        try
        {
            if (!visible) return;
            ImGui.TextUnformatted("DSR Meteor Pilot");
            ImGui.SameLine();
            ImGui.TextDisabled("v0.2.7");
            ImGui.TextWrapped("絶竜詩戦争 P2 · メテオ移動サポート");
            ImGui.Spacing();
            Section("自動移動");
            ImGui.TextColored(config.Enabled ? new Vector4(0.35f, 0.85f, 0.65f, 1) : new Vector4(0.7f, 0.75f, 0.8f, 1),
                config.Enabled ? "ON · ギミック開始に合わせて移動" : "OFF · 状態の確認のみ");
            // Use the same command handlers so UI cannot bypass readiness or encounter checks.
            if (!config.Enabled && ImGui.Button("自動移動をオン", new Vector2(180, 0))) OnCommand("/dsrmeteor", "on");
            if (config.Enabled && ImGui.Button("自動移動をオフ", new Vector2(180, 0))) OnCommand("/dsrmeteor", "off");
            ImGui.SameLine();
            if (ImGui.Button("今回の移動を停止", new Vector2(180, 0))) OnCommand("/dsrmeteor", "stop");
            if (manualStop) ImGui.TextWrapped("今回は停止済みです。戦闘終了後に停止状態が解除されます。");
            if (events == null) ImGui.TextWrapped("ギミック情報を取得できません。プラグインを再読み込みしてください。");
            ImGui.Spacing();
            Section("進行状況");
            ImGui.TextWrapped(encounter.Status);
            ImGui.ProgressBar(encounter.ConfirmedHits / 7f, new Vector2(-1, 0), $"メテオ {encounter.ConfirmedHits} / 7");
            ImGui.Spacing();
            Section("設定");
            var antiKnockback = config.AutoAntiKnockback;
            if (ImGui.Checkbox("アームズレングス／堅実魔を自動使用", ref antiKnockback))
                OnCommand("/dsrmeteor", antiKnockback ? "kb on" : "kb off");
            ImGui.TextDisabled("設定は自動で保存されます。");
            ImGui.Spacing();
            DrawDependencies();
            ImGui.Spacing();
            if (ImGui.CollapsingHeader("使い方"))
            {
                ImGui.TextWrapped("1. 最初の担当塔へ手動で移動します。\n2. ギミック開始前に自動移動をオンにします。\n3. 南北から時計回りに進み、反対側の南北塔へ向かいます。");
                ImGui.TextWrapped("移動を止めるときは「今回の移動を停止」、または /dsrmeteor stop を使用してください。");
            }
            if (ImGui.CollapsingHeader("診断情報"))
            {
                ImGui.TextUnformatted("ギミック情報の受信: " + (events != null ? "利用可能" : "利用不可"));
                ImGui.TextUnformatted(encounter.LastInterval > 0 ? $"直近の着弾間隔: {encounter.LastInterval:F3} 秒" : "直近の着弾間隔: —");
                var record = config.RecordTelemetry;
                if (ImGui.Checkbox("診断ログを保存", ref record))
                    OnCommand("/dsrmeteor", record ? "record on" : "record off");
                ImGui.TextWrapped("トラブルの確認に使う記録を、このPCに保存します。");
            }
        }
        finally { ImGui.End(); }
    }
}




