import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts
import "../components" as Comp

Rectangle {
    id: root
    property var env  // dict from EnvironmentBridge
    property var processBridge
    property var envBridge

    // ============ M2 NEW: node management ============
    property string currentEnvId: env ? env.id : ""
    property var nodeList: []
    property var conflictList: []

    Connections {
        target: appContext.node_bridge
        function onNodeListChanged() {
            if (currentEnvId) {
                nodeList = appContext.node_bridge.nodeList(currentEnvId);
            }
        }
    }

    Connections {
        target: appContext.node_bridge
        function onConflictListChanged() {
            if (currentEnvId) {
                conflictList = appContext.node_bridge.conflictList(currentEnvId);
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
            appContext.node_bridge.setScannedService(
                appContext.scanned_node_service(currentEnvId));
            appContext.node_bridge.requestScan(currentEnvId);
            nodeList = appContext.node_bridge.nodeList(currentEnvId);
            conflictList = appContext.node_bridge.conflictList(currentEnvId);
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
                appContext.node_bridge.resolveConflict(conflictId);
            }
            onIgnoreClicked: function(conflictId) {
                appContext.node_bridge.ignoreConflict(conflictId);
            }
            onDisableNodeClicked: function(nodeId) {
                appContext.node_bridge.toggleDisabled(nodeId);
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
            busy: appContext.node_bridge.busy
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
                onClicked: appContext.node_bridge.requestScan(currentEnvId)
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
                onToggleDisabledClicked: appContext.node_bridge.toggleDisabled(modelData.id)
            }
        }

        Comp.NodeDetailPanel {
            id: nodeDetailPanel
            Layout.fillWidth: true
        }
        // ============ M2 END ============

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
