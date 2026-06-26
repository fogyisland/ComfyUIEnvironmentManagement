import QtQuick 2.15
import QtQuick.Controls 2.15
import QtQuick.Layouts 1.15

Drawer {
    id: root
    property var node: ({})
    property var localDetail: ({})
    property var remoteDetail: null
    property bool fetchingRemote: false

    function openWith(nodeData) {
        node = nodeData;
        // 触发本地详情查(走 bridge)
        var r = appContext.node_bridge.getNodeDetail(nodeData.id);
        if (r.ok) {
            localDetail = r.value.local;
            remoteDetail = r.value.remote;
        }
        open();
    }

    width: Math.min(parent.width * 0.6, 480)
    height: parent.height
    edge: Qt.RightEdge

    ColumnLayout {
        anchors.fill: parent
        anchors.margins: 16
        spacing: 12

        // 标题
        Label {
            text: node.package || ""
            font.pixelSize: 20
            font.bold: true
        }

        // === 本地区 ===
        Label { text: qsTr("本地信息"); font.bold: true; font.pixelSize: 14 }
        GridLayout {
            columns: 2
            columnSpacing: 12
            rowSpacing: 4
            Label { text: qsTr("版本:") }
            Label { text: localDetail.version || qsTr("未知") }
            Label { text: qsTr("作者:") }
            Label { text: localDetail.author || qsTr("未知") }
            Label { text: qsTr("描述:") }
            Label { text: localDetail.description || qsTr("无"); wrapMode: Text.Wrap; Layout.fillWidth: true }
            Label { text: qsTr("类映射:") }
            Label {
                text: (localDetail.class_mappings || []).join(", ") || qsTr("无")
                wrapMode: Text.Wrap
                Layout.fillWidth: true
            }
        }

        // warnings
        Repeater {
            model: (localDetail.scan_meta && localDetail.scan_meta.warnings) || []
            delegate: Label {
                text: "⚠ " + modelData
                color: "#B3261E"
                font.pixelSize: 12
                wrapMode: Text.Wrap
                Layout.fillWidth: true
            }
        }

        Divider { Layout.fillWidth: true }

        // === 远程区 ===
        Label { text: qsTr("远程信息 (GitHub)"); font.bold: true; font.pixelSize: 14 }
        RowLayout {
            Layout.fillWidth: true
            Button {
                text: fetchingRemote ? qsTr("获取中…") : qsTr("查看远程信息")
                enabled: !fetchingRemote
                onClicked: {
                    fetchingRemote = true;
                    // M2: 暂时用占位 owner/repo,实际 M3+ 让用户填或从 pyproject 提
                    var r = appContext.node_bridge.fetchRemoteMeta(
                        node.package, "unknown-owner", node.package);
                    fetchingRemote = false;
                    if (r.ok) {
                        remoteDetail = r.value;
                    } else {
                        // 失败也保持 UI 可用
                        remoteDetail = {fetch_error: r.error.message};
                    }
                }
            }
        }
        GridLayout {
            visible: remoteDetail !== null
            columns: 2
            columnSpacing: 12
            rowSpacing: 4
            Label { text: qsTr("Stars:") }
            Label { text: remoteDetail && remoteDetail.stars !== null && remoteDetail.stars !== undefined ? remoteDetail.stars : qsTr("未知") }
            Label { text: qsTr("最后提交:") }
            Label { text: remoteDetail && remoteDetail.last_commit ? remoteDetail.last_commit : qsTr("未知") }
            Label { text: qsTr("主页:") }
            Label {
                text: remoteDetail && remoteDetail.homepage ? remoteDetail.homepage : qsTr("无")
                wrapMode: Text.Wrap
                Layout.fillWidth: true
            }
        }
        Label {
            visible: remoteDetail && remoteDetail.fetch_error
            text: qsTr("⚠ 无法获取远程信息: %1").arg(remoteDetail ? remoteDetail.fetch_error : "")
            color: "#B3261E"
            font.pixelSize: 12
            wrapMode: Text.Wrap
            Layout.fillWidth: true
        }

        Item { Layout.fillHeight: true }  // spacer

        Button {
            text: qsTr("关闭")
            Layout.alignment: Qt.AlignRight
            onClicked: root.close()
        }
    }
}
