import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts
import "../components" as Comp

Dialog {
    id: root
    property var envBridge
    title: qsTr("新建环境")
    modal: true
    standardButtons: Dialog.NoButton
    width: 480
    anchors.centerIn: parent

    function _validate() {
        if (!nameField.text.trim()) {
            errorLabel.text = qsTr("请输入环境名")
            return false
        }
        if (!pythonField.text.trim()) {
            errorLabel.text = qsTr("请选择 Python 解释器")
            return false
        }
        if (layoutCombo.currentText === qsTr("共享 ComfyUI") && !comfyuiField.text.trim()) {
            errorLabel.text = qsTr("共享布局必须指定 ComfyUI 源")
            return false
        }
        errorLabel.text = ""
        return true
    }

    function _submit() {
        if (!_validate()) return
        const result = envBridge.createEnv(
            nameField.text.trim(),
            layoutCombo.currentIndex === 0 ? "shared" : "independent",
            pythonField.text.trim(),
            comfyuiField.text.trim(),
            parseInt(portField.text) || 8188
        )
        if (result.ok) {
            root.close()
        } else {
            errorLabel.text = result.error.message
        }
    }

    ColumnLayout {
        anchors.fill: parent
        spacing: 12

        Comp.FormField {
            label: qsTr("环境名")
            Layout.fillWidth: true
            TextField {
                id: nameField
                Layout.fillWidth: true
                placeholderText: qsTr("例如: my-env")
            }
        }

        Comp.FormField {
            label: qsTr("布局")
            Layout.fillWidth: true
            ComboBox {
                id: layoutCombo
                Layout.fillWidth: true
                model: [qsTr("共享 ComfyUI"), qsTr("独立 ComfyUI")]
            }
        }

        Comp.FormField {
            label: qsTr("Python 解释器")
            Layout.fillWidth: true
            Comp.PathField {
                id: pythonField
                Layout.fillWidth: true
                fileFilter: "Python (python.exe python);;All files (*)"
            }
        }

        Comp.FormField {
            label: qsTr("ComfyUI 源 (共享布局必填)")
            Layout.fillWidth: true
            visible: layoutCombo.currentIndex === 0
            Comp.PathField {
                id: comfyuiField
                Layout.fillWidth: true
                fileMode: "directory"
            }
        }

        Comp.FormField {
            label: qsTr("端口")
            Layout.fillWidth: true
            TextField {
                id: portField
                Layout.fillWidth: true
                text: "8188"
                inputMethodHints: Qt.ImhDigitsOnly
                validator: IntValidator { bottom: 1; top: 65535 }
            }
        }

        Text {
            id: errorLabel
            color: Theme.color("error")
            visible: text !== ""
            Layout.fillWidth: true
            wrapMode: Text.WordWrap
        }

        RowLayout {
            Layout.fillWidth: true
            Item { Layout.fillWidth: true }
            Button {
                text: qsTr("取消")
                onClicked: root.close()
            }
            Button {
                text: qsTr("创建")
                Material.background: Theme.color("primary")
                Material.foreground: Theme.color("onPrimary")
                onClicked: root._submit()
            }
        }
    }
}