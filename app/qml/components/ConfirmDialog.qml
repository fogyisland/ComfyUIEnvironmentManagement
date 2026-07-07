import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts
import Manager 1.0

Dialog {
    id: root
    property string message: ""
    property string confirmText: qsTr("确认")
    property string cancelText: qsTr("取消")
    property var onConfirm: null
    property var onCancel: null

    title: qsTr("请确认")
    modal: true
    standardButtons: Dialog.NoButton
    anchors.centerIn: parent
    width: 400

    ColumnLayout {
        anchors.fill: parent
        spacing: 12

        Text {
            text: root.message
            color: Theme.color("onSurface")
            wrapMode: Text.WordWrap
            Layout.fillWidth: true
        }

        RowLayout {
            Layout.fillWidth: true
            Item { Layout.fillWidth: true }
            Button {
                text: root.cancelText
                onClicked: {
                    if (root.onCancel) root.onCancel()
                    root.close()
                }
            }
            Button {
                text: root.confirmText
                Material.background: Theme.color("primary")
                Material.foreground: Theme.color("onPrimary")
                onClicked: {
                    if (root.onConfirm) root.onConfirm()
                    root.close()
                }
            }
        }
    }
}