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
