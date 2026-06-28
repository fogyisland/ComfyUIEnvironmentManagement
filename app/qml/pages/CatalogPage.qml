import QtQuick 2.15
import QtQuick.Controls 2.15
import QtQuick.Layouts 1.15

import "../components" as Comp

Page {
    id: root
    title: qsTr("节点目录")

    property var envList: []
    property var entries: []
    property bool isStale: false
    property string staleMessage: ""

    function refresh() {
        var r = appContext.node_bridge.refreshCatalog();
        if (r.ok) {
            root.entries = r.value;
            root.isStale = false;
        } else {
            // 离线模式已经在 bridge 里降级:entries 仍可用 + stale 标记
            root.entries = r.value || [];
            root.isStale = true;
            root.staleMessage = r.error ? r.error.code : "";
        }
    }

    function search(q) {
        var r = appContext.node_bridge.searchCatalog(q, 1);
        if (r.ok) root.entries = r.value;
    }

    function openInstall(entry) {
        installDialog.catalogEntry = entry;
        installDialog.envList = root.envList;
        installDialog.open();
    }

    Component.onCompleted: root.refresh()

    header: ToolBar {
        RowLayout {
            anchors.fill: parent
            TextField {
                id: searchField
                Layout.fillWidth: true
                placeholderText: qsTr("搜索节点 (e.g. impact, manager)...")
                onAccepted: root.search(text)
            }
            Button {
                text: qsTr("刷新")
                onClicked: root.refresh()
            }
        }
    }

    ColumnLayout {
        anchors.fill: parent
        spacing: 0

        // 离线提示条
        Rectangle {
            visible: root.isStale
            Layout.fillWidth: true
            height: 32
            color: "#FFF3CD"
            Label {
                anchors.centerIn: parent
                text: qsTr("⚠ 离线 — 显示缓存数据 (%1)").arg(root.staleMessage)
                color: "#856404"
            }
        }

        ScrollView {
            Layout.fillWidth: true
            Layout.fillHeight: true
            GridView {
                id: grid
                model: root.entries
                cellWidth: 220
                cellHeight: 180
                clip: true
                delegate: Rectangle {
                    width: grid.cellWidth - 8
                    height: grid.cellHeight - 8
                    color: Theme.color("surface")
                    border.color: Theme.color("outline")
                    radius: 8

                    ColumnLayout {
                        anchors.fill: parent
                        anchors.margins: 10
                        spacing: 4

                        Label {
                            text: modelData.name || modelData.id
                            font.bold: true
                            elide: Text.ElideRight
                            Layout.fillWidth: true
                        }
                        Label {
                            text: modelData.author || ""
                            color: Theme.color("outline")
                            font.pixelSize: 11
                        }
                        Label {
                            visible: modelData.stars !== undefined
                            text: qsTr("⭐ %1").arg(modelData.stars)
                            font.pixelSize: 11
                        }
                        Label {
                            text: modelData.description || ""
                            wrapMode: Text.Wrap
                            elide: Text.ElideRight
                            maximumLineCount: 3
                            Layout.fillWidth: true
                            Layout.fillHeight: true
                            font.pixelSize: 11
                        }
                        Button {
                            text: qsTr("安装")
                            Layout.fillWidth: true
                            onClicked: root.openInstall(modelData)
                        }
                    }
                }
            }
        }

        Label {
            visible: root.entries.length === 0
            text: qsTr("(无数据 — 点刷新按钮)")
            color: Theme.color("outline")
            Layout.alignment: Qt.AlignHCenter
        }
    }

    Comp.InstallDialog {
        id: installDialog
        onInstallRequested: function(envId) {
            installDialog.busyIndicatorRunning = true;
            var r = appContext.node_bridge.installFromCatalog(
                installDialog.catalogEntry.id, envId);
            installDialog.busyIndicatorRunning = false;
            if (r.ok) {
                installDialog.close();
            }
        }
        property alias busyIndicatorRunning: installDialog.busyIndicator.running
    }
}
