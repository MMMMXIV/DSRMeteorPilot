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
                    dependencyStatus = "◯ インストール済み（未ロード）";
                    dependencyAdvice = "vnavmeshを有効化してください。読み込みに失敗している場合は、プラグイン一覧で状態を確認してください。";
                }
                else
                {
                    dependencyStatus = "◯ インストール済み・ロード済み";
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
        ImGui.TextUnformatted("必要なプラグイン");
        ImGui.TextWrapped("vnavmesh（自動移動用）: " + dependencyStatus);
        ImGui.TextWrapped(dependencyAdvice);
        ImGui.TextWrapped("導入状態はメニューを開いた時点の情報です。更新するには閉じて開き直してください。");
        ImGui.TextWrapped("SplatoonやBossModは、このプラグインの動作には必要ありません。");
    }

    private void DrawUi()
    {
        if (!uiOpen) return;
        ImGui.SetNextWindowSize(new Vector2(600, 530), ImGuiCond.FirstUseEver);
        var visible = ImGui.Begin("DSR Meteor Pilot — 設定・状態###DSRMeteorPilot", ref uiOpen);
        try
        {
            if (!visible) return;
            ImGui.TextUnformatted("DSR Meteor Pilot v0.2.5");
            ImGui.TextWrapped("南北・時計回り処理用。最初の担当塔への散開は手動です。実機でのギミック成功は未検証です。");
            ImGui.Separator();
            DrawDependencies();
            ImGui.Separator();
            ImGui.TextWrapped("状態: " + encounter.Status);
            ImGui.TextUnformatted($"メテオ着弾: {encounter.ConfirmedHits}/7   直近間隔: {encounter.LastInterval:F3}秒");
            ImGui.TextUnformatted("イベント受信初期化: " + (events != null ? "成功" : "失敗"));
            ImGui.TextUnformatted("自動移動設定: " + (config.Enabled ? "有効" : "無効（観測のみ）"));
            if (manualStop) ImGui.TextWrapped("このプルは停止済みです。戦闘終了後に停止状態を解除します。");
            ImGui.Separator();
            // Use the same command handlers so UI cannot bypass readiness or encounter checks.
            if (!config.Enabled && ImGui.Button("自動移動を有効化")) OnCommand("/dsrmeteor", "on");
            if (config.Enabled && ImGui.Button("自動移動を無効化")) OnCommand("/dsrmeteor", "off");
            ImGui.SameLine();
            if (ImGui.Button("このプルの移動を停止")) OnCommand("/dsrmeteor", "stop");
            var antiKnockback = config.AutoAntiKnockback;
            if (ImGui.Checkbox("アームズレングス／堅実魔を自動使用", ref antiKnockback))
                OnCommand("/dsrmeteor", antiKnockback ? "kb on" : "kb off");
            var record = config.RecordTelemetry;
            if (ImGui.Checkbox("診断記録を保存", ref record))
                OnCommand("/dsrmeteor", record ? "record on" : "record off");
            ImGui.TextWrapped("自動移動の有効・無効は保存されます。異常時は /dsrmeteor stop でも停止できます。");
        }
        finally { ImGui.End(); }
    }
}




