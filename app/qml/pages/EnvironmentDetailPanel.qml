import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts
import "../components" as Comp
import Manager 1.0

Rectangle {
    id: root
    property var env  // dict from EnvironmentBridge
    // processBridge / envBridge 来自 rootContext,直接用全局名字 — 不要声明同名
    // property var,会 shadow rootContext 导致 binding 取到 undefined。

    // ============ M2 NEW: node management ============
    property string currentEnvId: env ? env.id : ""
    property var nodeList: []
    property var conflictList: []

    // ============ M3 NEW: version / dep data ============
    property var versionList: []
    property var depList: []

    Connections {
        target: nodeBridge
        function onNodeListChanged() {
            if (currentEnvId) {
                nodeList = nodeBridge.nodeList(currentEnvId);
                // M3: rescan 完成后 nodeList 变化,自动刷新版本/依赖视图
                if (typeof versionPanel !== "undefined" && versionPanel) {
                    versionPanel.refresh();
                }
                if (typeof depPanel !== "undefined" && depPanel) {
                    depPanel.refreshDeps();
                }
            }
        }
    }

    Connections {
        target: nodeBridge
        function onConflictListChanged() {
            if (currentEnvId) {
                conflictList = nodeBridge.conflictList(currentEnvId);
            }
        }
    }

    function _findNodeById(nodeId) {
        for (var i = 0; i < nodeList.length; i++) {
            if (nodeList[i].id === nodeId) {
                return nodeList[i];
            }
        }
        return null;
    }

    Component.onCompleted: {
        if (currentEnvId) {
            // M2 review Critical 修复:NodeBridge.scanned 是 per-env 实例,
            // 切到当前 env 时必须先装上,否则 requestScan / nodeList / 等
            // 全部 AttributeError。
            nodeBridge.setScannedService(
                scannedServiceFactory.make(currentEnvId));
            nodeBridge.requestScan(currentEnvId);
            nodeList = nodeBridge.nodeList(currentEnvId);
            conflictList = nodeBridge.conflictList(currentEnvId);
        }
    }
    // ============ M2 END ============

    color: Theme.color("surface")
    border.color: Theme.color("outline")
    border.width: 1
    radius: 8

    ColumnLayout {
        anchors.fill: parent
        anchors.margins: 16
        spacing: 12

        // === EnvInfoCard ===
        ColumnLayout {
            Layout.fillWidth: true
            spacing: 4
            Text {
                text: env ? env.name : ""
                font.pixelSize: 24
                font.bold: true
                color: Theme.color("onSurface")
            }
            RowLayout {
                spacing: 8
                Comp.StatusIndicator { status: env ? env.status : "stopped" }
                Text {
                    text: env ? (env.status === "running" ? qsTr("运行中") : qsTr("已停止")) : ""
                    color: Theme.color("onSurfaceVariant")
                }
            }
            Text {
                text: env ? qsTr("端口: %1").arg(env.port) : ""
                color: Theme.color("onSurfaceVariant")
            }
            Text {
                text: env ? qsTr("Python: %1").arg(env.pythonExecutable) : ""
                color: Theme.color("onSurfaceVariant")
                font.pixelSize: 11
                elide: Text.ElideMiddle
                Layout.fillWidth: true
            }
        }

        // === ActionRow ===
        RowLayout {
            spacing: 8
            Button {
                text: qsTr("启动")
                enabled: env && env.status !== "running"
                Material.background: Theme.color("primary")
                Material.foreground: Theme.color("onPrimary")
                onClicked: processBridge.startEnv(env.id)
            }
            Button {
                text: qsTr("停止")
                enabled: env && env.status === "running"
                onClicked: processBridge.stopEnv(env.id, 10.0)
            }
            Button {
                text: qsTr("删除")
                onClicked: envBridge.deleteEnv(env.id, false)
            }
        }

        // ============ M2 NEW: 节点管理 ============
        Comp.ConflictPanel {
            Layout.fillWidth: true
            conflicts: conflictList
            currentEnvId: currentEnvId
            onResolveClicked: function(conflictId) {
                nodeBridge.resolveConflict(conflictId);
            }
            onIgnoreClicked: function(conflictId) {
                nodeBridge.ignoreConflict(conflictId);
            }
            onDisableNodeClicked: function(nodeId) {
                nodeBridge.toggleDisabled(nodeId);
            }
            onViewNodeClicked: function(nodeId) {
                var n = _findNodeById(nodeId);
                if (n) {
                    nodeDetailPanel.openWith(n);
                }
            }
        }

        Comp.NodeScanBusy {
            Layout.fillWidth: true
            busy: nodeBridge.busy
        }

        Label {
            Layout.fillWidth: true
            text: qsTr("自定义节点 (%1)").arg(nodeList.length)
            font.bold: true
            font.pixelSize: 14
        }

        RowLayout {
            Layout.fillWidth: true
            Button {
                text: qsTr("重新扫描")
                onClicked: nodeBridge.requestScan(currentEnvId)
            }
            Item { Layout.fillWidth: true }
        }

        ListView {
            Layout.fillWidth: true
            Layout.preferredHeight: 280
            model: nodeList
            delegate: Comp.NodeListItem {
                width: ListView.view.width
                node: modelData
                onClicked: nodeDetailPanel.openWith(modelData)
                onToggleDisabledClicked: nodeBridge.toggleDisabled(modelData.id)
            }
        }

        Comp.NodeDetailPanel {
            id: nodeDetailPanel
            Layout.fillWidth: true
        }
        // ============ M2 END ============

        // ============ M3 NEW: VersionPanel + DepPanel + HistoryDialog ============
        Comp.VersionPanel {
            id: versionPanel
            Layout.fillWidth: true
            currentEnvId: root.currentEnvId
            visible: root.nodeList.length > 0
            versionList: root.versionList

            function refresh() {
                var list = [];
                for (var i = 0; i < root.nodeList.length; i++) {
                    var pkg = root.nodeList[i].package || "";
                    if (!pkg) continue;
                    var s = nodeBridge.listVersions(root.currentEnvId, pkg);
                    if (s.ok && s.value.length > 0) {
                        list.push(s.value[0]);
                    }
                }
                root.versionList = list;
            }

            hasUpdatable: {
                for (var i = 0; i < root.versionList.length; i++) {
                    if (root.versionList[i].has_update && !root.versionList[i].locked) {
                        return true;
                    }
                }
                return false;
            }

            onRefreshRequested: refresh()
            onHistoryRequested: function(packageName) {
                historyDialog.load(root.currentEnvId, packageName);
                historyDialog.open();
            }

            Component.onCompleted: refresh()
        }

        Comp.DepPanel {
            id: depPanel
            Layout.fillWidth: true
            currentEnvId: root.currentEnvId
            globalCompatEnabled: false
            depList: root.depList
            conflictList: root.conflictList

            function refreshDeps() {
                var r = nodeBridge.listDeps(root.currentEnvId, "");
                if (r.ok) root.depList = r.value;
                var c = nodeBridge.detectDepConflicts(root.currentEnvId);
                if (c.ok) root.conflictList = c.value;
            }

            function rescanAll() {
                for (var i = 0; i < root.nodeList.length; i++) {
                    var pkg = root.nodeList[i].package || "";
                    if (pkg) {
                        nodeBridge.scanDeps(root.currentEnvId, pkg);
                    }
                }
                refreshDeps();
            }

            onRescanAllRequested: rescanAll()

            Component.onCompleted: refreshDeps()
        }

        Comp.HistoryDialog {
            id: historyDialog
            onRollbackRequested: function(historyId) {
                var r = nodeBridge.rollbackVersion(
                    root.currentEnvId, historyDialog.packageName, historyId);
                if (r.ok) historyDialog.close();
            }
        }

        Connections {
            target: nodeBridge
            function onVersionChanged(envId, pkg) {
                if (envId === root.currentEnvId) versionPanel.refresh();
            }
            function onDepsChanged(envId, pkg) {
                if (envId === root.currentEnvId) depPanel.refreshDeps();
            }
        }
        // ============ M3 END ============

        // === LogSection ===
        Text {
            text: qsTr("实时日志")
            font.bold: true
            color: Theme.color("onSurface")
        }
        Comp.LogViewer {
            id: logViewer
            Layout.fillWidth: true
            Layout.fillHeight: true
            // Bind to processBridge.logVersion so QML re-evaluates logsFor()
            // when processLogLine fires (logsFor is a @Slot, not a notify
            // property — without this dep the binding only runs once).
            logLines: {
                // dummy dependency on processBridge.logVersion
                processBridge.logVersion
                return env ? processBridge.logsFor(env.id) : []
            }
        }
    }
}
