import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts
import "pages" as Pages
import "components" as Comp

ApplicationWindow {
    id: window
    width: 1200
    height: 800
    visible: true
    title: qsTr("ComfyUI Manager")

    // === Header (顶部栏) ===
    header: ToolBar {
        Material.background: Theme.color("primary")
        RowLayout {
            anchors.fill: parent
            anchors.leftMargin: 12
            anchors.rightMargin: 12
            spacing: 12

            ToolButton {
                text: "≡"
                font.pixelSize: 20
                Material.foreground: Theme.color("onPrimary")
                onClicked: drawer.open()
            }
            Text {
                text: qsTr("ComfyUI Manager")
                font.pixelSize: 18
                font.bold: true
                color: Theme.color("onPrimary")
                Layout.fillWidth: true
            }
            // LanguageSwitch
            ComboBox {
                id: languageSwitch
                model: ["zh_CN", "en_US"]
                currentIndex: settingsBridge.current.language === "en_US" ? 1 : 0
                onActivated: settingsBridge.setValue("language", currentText)
            }
        }
    }

    // === NavigationDrawer ===
    Drawer {
        id: drawer
        width: Math.min(window.width * 0.5, 280)
        height: window.height
        Material.background: Theme.color("surface")

        ColumnLayout {
            anchors.fill: parent
            anchors.topMargin: 16
            spacing: 0

            Item { height: 8 }

            ListView {
                id: navList
                Layout.fillWidth: true
                Layout.fillHeight: true
                model: [
                    {key: "environments", label: qsTr("环境管理"), icon: "🗂"},
                    {key: "catalog",     label: qsTr("节点目录"), icon: "📦"},
                    {key: "settings",    label: qsTr("设置"),     icon: "⚙"},
                ]
                currentIndex: 0
                delegate: ItemDelegate {
                    width: ListView.view.width
                    height: 48
                    highlighted: ListView.isCurrentItem
                    onClicked: {
                        navList.currentIndex = index
                        stack.currentIndex = index
                        drawer.close()
                    }
                    contentItem: RowLayout {
                        spacing: 12
                        Text { text: modelData.icon; font.pixelSize: 18 }
                        Text { text: modelData.label; color: Theme.color("onSurface") }
                    }
                }
            }
        }
    }

    // === StackLayout 主内容 ===
    StackLayout {
        id: stack
        anchors.fill: parent
        currentIndex: navList.currentIndex

        Pages.EnvironmentPage {
            envBridge: envBridge
            processBridge: processBridge
        }
        Pages.CatalogPage {
            envList: envBridge.envList
        }
        Pages.SettingsPage {
            settingsBridge: settingsBridge
        }
    }

    // === 全局错误订阅 ===
    Connections {
        target: envBridge
        function onErrorOccurred(code, message) { globalError.show(code, message) }
    }
    Connections {
        target: processBridge
        function onErrorOccurred(code, message) { globalError.show(code, message) }
    }
    Connections {
        target: catalogBridge
        function onErrorOccurred(code, message) { globalError.show(code, message) }
    }
    Connections {
        target: settingsBridge
        function onErrorOccurred(code, message) { globalError.show(code, message) }
    }
    Connections {
        target: torchBridge
        function onErrorOccurred(code, message) { globalError.show(code, message) }
    }
    Connections {
        target: nodeBridge
        function onErrorOccurred(code, message) { globalError.show(code, message) }
    }

    Comp.ErrorBanner {
        id: globalError
        anchors.top: parent.top
        anchors.left: parent.left
        anchors.right: parent.right
        z: 10000
    }

    // === 主题模式响应 ===
    Connections {
        target: settingsBridge
        function onThemeModeChanged(mode) {
            Theme.mode = mode
        }
    }
    Component.onCompleted: {
        Theme.mode = settingsBridge.current.theme_mode || "system"
    }
}
