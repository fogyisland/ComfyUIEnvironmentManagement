import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts

Rectangle {
    id: root
    property string message: ""
    property string code: ""
    signal dismissed()

    color: Theme.color("error")
    radius: 4
    height: visible ? layout.height + 24 : 0
    visible: message !== ""
    Behavior on height { NumberAnimation { duration: 200 } }

    function show(errCode, errMessage) {
        code = errCode
        message = errMessage
        hideTimer.restart()
    }

    Timer {
        id: hideTimer
        interval: 6000
        onTriggered: root.dismissed()
    }

    RowLayout {
        id: layout
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.verticalCenter: parent.verticalCenter
        anchors.leftMargin: 12
        anchors.rightMargin: 12
        spacing: 8

        ColumnLayout {
            Layout.fillWidth: true
            spacing: 2
            Text {
                text: root.code
                font.bold: true
                font.pixelSize: 12
                color: "white"
            }
            Text {
                text: root.message
                color: "white"
                wrapMode: Text.WordWrap
                Layout.fillWidth: true
            }
        }

        Button {
            text: "✕"
            flat: true
            onClicked: root.dismissed()
        }
    }

    onDismissed: {
        message = ""
        code = ""
    }
}
