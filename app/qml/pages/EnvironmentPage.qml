import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts
import "../components" as Comp
import "."

Item {
    id: root
    property var envBridge
    property var processBridge

    Component.onCompleted: envBridge.envListChanged

    SplitView {
        anchors.fill: parent
        orientation: Qt.Horizontal

        // === Left: Env List (30%) ===
        Rectangle {
            SplitView.preferredWidth: root.width * 0.3
            SplitView.minimumWidth: 200
            color: Theme.color("background")

            ColumnLayout {
                anchors.fill: parent
                spacing: 0

                TextField {
                    id: searchField
                    Layout.fillWidth: true
                    Layout.margins: 8
                    placeholderText: qsTr("搜索环境...")
                }

                ListView {
                    id: envListView
                    Layout.fillWidth: true
                    Layout.fillHeight: true
                    model: envBridge.envList
                    clip: true
                    spacing: 1

                    delegate: Rectangle {
                        width: envListView.width
                        height: 56
                        color: ListView.isCurrentItem ? Theme.color("primaryContainer") : Theme.color("surface")
                        border.color: Theme.color("outline")
                        border.width: 0

                        MouseArea {
                            anchors.fill: parent
                            onClicked: envListView.currentIndex = index
                        }

                        RowLayout {
                            anchors.fill: parent
                            anchors.margins: 12
                            spacing: 8

                            Comp.StatusIndicator { status: modelData.status }

                            ColumnLayout {
                                spacing: 2
                                Layout.fillWidth: true
                                Text {
                                    text: modelData.name
                                    font.bold: true
                                    color: Theme.color("onSurface")
                                    elide: Text.ElideRight
                                    Layout.fillWidth: true
                                }
                                Text {
                                    text: qsTr("端口 %1").arg(modelData.port)
                                    font.pixelSize: 11
                                    color: Theme.color("onSurfaceVariant")
                                }
                            }
                        }
                    }

                    onCurrentIndexChanged: {
                        if (currentIndex >= 0) {
                            const env = envBridge.envList[currentIndex]
                            envBridge.getEnv(env.id)  // refresh
                        }
                    }
                }
            }
        }

        // === Right: Detail (70%) ===
        Item {
            SplitView.fillWidth: true

            StackLayout {
                anchors.fill: parent
                currentIndex: envListView.currentIndex >= 0 ? 1 : 0

                // EmptyState
                ColumnLayout {
                    spacing: 8
                    Text {
                        text: qsTr("选择环境查看详情")
                        color: Theme.color("onSurfaceVariant")
                        font.pixelSize: 16
                        Layout.alignment: Qt.AlignCenter
                    }
                }

                // DetailPanel
                EnvironmentDetailPanel {
                    env: envListView.currentIndex >= 0 ? envBridge.envList[envListView.currentIndex] : null
                    processBridge: root.processBridge
                    envBridge: root.envBridge
                }
            }
        }
    }

    // === FAB: 新建环境 ===
    RoundButton {
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        anchors.margins: 24
        text: "+"
        Material.background: Theme.color("primary")
        Material.foreground: Theme.color("onPrimary")
        onClicked: createEnvDialog.open()
    }

    CreateEnvDialog {
        id: createEnvDialog
        envBridge: root.envBridge
    }

    // Note: error banner for envBridge + processBridge is wired globally
    // in Main.qml's `globalError` Connections block. No local ErrorBanner
    // here to avoid stacked banners.
}
