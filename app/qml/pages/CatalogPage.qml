import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts
import "../components" as Comp

Item {
    id: root
    property var catalogBridge

    ColumnLayout {
        anchors.fill: parent
        anchors.margins: 16
        spacing: 12

        // === HeaderBar ===
        RowLayout {
            Layout.fillWidth: true
            spacing: 8

            TextField {
                id: urlField
                Layout.fillWidth: true
                placeholderText: qsTr("GitHub 仓库 URL (例如 https://github.com/owner/repo)")
                onAccepted: addBtn.clicked()
            }
            Button {
                id: addBtn
                text: qsTr("添加")
                Material.background: Theme.color("primary")
                Material.foreground: Theme.color("onPrimary")
                onClicked: {
                    if (urlField.text.trim()) {
                        const result = catalogBridge.addNode(urlField.text.trim())
                        if (result.ok) {
                            urlField.text = ""
                        } else {
                            addError.text = result.error.message
                        }
                    }
                }
            }
        }

        Text {
            id: addError
            color: Theme.color("error")
            visible: text !== ""
            Layout.fillWidth: true
        }

        // === List ===
        ListView {
            Layout.fillWidth: true
            Layout.fillHeight: true
            model: catalogBridge.nodeList
            clip: true
            spacing: 1

            delegate: Rectangle {
                width: ListView.view.width
                height: 64
                color: Theme.color("surface")
                border.color: Theme.color("outline")
                border.width: 0

                RowLayout {
                    anchors.fill: parent
                    anchors.margins: 12
                    spacing: 12

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
                            text: modelData.repoUrl
                            font.pixelSize: 11
                            color: Theme.color("onSurfaceVariant")
                            elide: Text.ElideMiddle
                            Layout.fillWidth: true
                        }
                    }

                    Button {
                        text: qsTr("删除")
                        onClicked: catalogBridge.removeNode(modelData.id)
                    }
                }
            }
        }
    }
}
