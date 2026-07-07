import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts
import "../components" as Comp
import Manager 1.0

ScrollView {
    id: root
    // 不在这里声明 property var settingsBridge — 会 shadow rootContext 注入的同名 context property。
    // 直接使用外层 rootContext 注入的 settingsBridge。
    clip: true

    ColumnLayout {
        width: root.width - 32
        x: 16
        y: 16
        spacing: 16

        Text {
            text: qsTr("设置")
            font.pixelSize: 24
            font.bold: true
            color: Theme.color("onBackground")
        }

        // === 数据库路径 ===
        Comp.FormField {
            label: qsTr("数据库路径")
            Layout.fillWidth: true
            helperText: qsTr("修改后点击「迁移」按钮复制现有数据库到新位置")
            ColumnLayout {
                spacing: 8
                Layout.fillWidth: true
                Comp.PathField {
                    id: dbPathField
                    Layout.fillWidth: true
                    fileMode: "directory"
                    path: settingsBridge.current.catalog_db_path || ""
                }
                RowLayout {
                    Button {
                        text: qsTr("迁移")
                        enabled: dbPathField.path !== (settingsBridge.current.catalog_db_path || "")
                        onClicked: {
                            const result = settingsBridge.migrateDbPath(dbPathField.path)
                            if (result.ok) {
                                migrateSuccess.text = qsTr("迁移成功，请重启应用")
                            } else {
                                migrateSuccess.text = ""
                            }
                        }
                    }
                    Text {
                        id: migrateSuccess
                        color: Theme.color("primary")
                    }
                }
            }
        }

        // === 主题模式 ===
        Comp.FormField {
            label: qsTr("主题模式")
            Layout.fillWidth: true
            ComboBox {
                Layout.fillWidth: true
                model: [qsTr("浅色"), qsTr("深色"), qsTr("跟随系统")]
                currentIndex: {
                    const m = settingsBridge.current.theme_mode
                    if (m === "light") return 0
                    if (m === "dark") return 1
                    return 2
                }
                onActivated: {
                    const val = ["light", "dark", "system"][currentIndex]
                    settingsBridge.setValue("theme_mode", val)
                }
            }
        }

        // === 主题 ===
        Comp.FormField {
            label: qsTr("主色调")
            Layout.fillWidth: true
            ComboBox {
                Layout.fillWidth: true
                model: ["Material 紫", "Material 蓝"]
                currentIndex: settingsBridge.current.theme === "material_blue" ? 1 : 0
                onActivated: {
                    const val = currentIndex === 1 ? "material_blue" : "material_purple"
                    settingsBridge.setValue("theme", val)
                }
            }
        }

        // === 语言 ===
        Comp.FormField {
            label: qsTr("语言")
            Layout.fillWidth: true
            ComboBox {
                Layout.fillWidth: true
                model: ["简体中文", "English"]
                currentIndex: settingsBridge.current.language === "en_US" ? 1 : 0
                onActivated: {
                    const val = currentIndex === 1 ? "en_US" : "zh_CN"
                    settingsBridge.setValue("language", val)
                }
            }
        }

        // === 日志级别 ===
        Comp.FormField {
            label: qsTr("日志级别")
            Layout.fillWidth: true
            ComboBox {
                Layout.fillWidth: true
                model: ["DEBUG", "INFO", "WARNING", "ERROR"]
                currentIndex: ["DEBUG", "INFO", "WARNING", "ERROR"].indexOf(
                    settingsBridge.current.log_level || "INFO")
                onActivated: settingsBridge.setValue("log_level", currentText)
            }
        }

        // === 节点目录自动更新 ===
        Comp.FormField {
            label: qsTr("节点目录自动更新")
            Layout.fillWidth: true
            helperText: qsTr("开启后,应用启动 + 后台定时自动从远端拉全量节点到本地缓存")
            RowLayout {
                Layout.fillWidth: true
                Switch {
                    id: autoRefreshSwitch
                    text: qsTr("启用")
                    checked: settingsBridge.current.catalog_auto_refresh !== false
                    onToggled: settingsBridge.setValue("catalog_auto_refresh", checked)
                }
                Item { Layout.fillWidth: true }
                Label { text: qsTr("间隔(分钟):") }
                SpinBox {
                    id: autoRefreshMinutes
                    from: 30
                    to: 4320  // 3 days
                    stepSize: 30
                    value: settingsBridge.current.catalog_auto_refresh_minutes || 360
                    onValueModified: settingsBridge.setValue("catalog_auto_refresh_minutes", value)
                }
            }
        }

        // === 节点管理 ===
        Comp.FormField {
            label: qsTr("禁用模式")
            Layout.fillWidth: true
            ComboBox {
                id: nodeDisableModeCombo
                Layout.fillWidth: true
                // M2 review Important #1 修复:folder_rename 模式 plumbing
                // 已在 ScannedNodeService.set_disabled 留 TODO,但实际目录
                // rename 未实现。UI 不该误导用户能切到这个无效模式 → 把
                // folder_rename 那一项 disabled,只剩 db_flag(M2 唯一可
                // 用模式)。M3 完整实现后再 enabled。
                // Qt 6.11 Windows style 下 ComboBox 配 textRole="text" + 字典数组
                // 报 "Unable to assign QVariantMap to QString",改用纯字符串数组 +
                // 自定义 enabled 通过 onActivated 时弹 ConfirmDialog 拦截。
                model: [
                    qsTr("仅标记 (推荐)"),
                    qsTr("重命名目录 (强制跳过) [M3+]"),
                ]
                currentIndex: {
                    var v = settingsBridge.current.node_disable_mode || "db_flag";
                    return v === "folder_rename" ? 1 : 0;
                }
                onActivated: {
                    if (currentIndex === 1) {
                        // 还原成 db_flag,不让用户切换到未实现模式
                        currentIndex = 0;
                        settingsBridge.setValue("node_disable_mode", "db_flag");
                    } else {
                        settingsBridge.setValue("node_disable_mode", "db_flag");
                    }
                }
            }
            helperText: qsTr("M2 阶段仅支持「仅标记」模式。folder_rename 模式计划在 M3 完整实现,届时会同步启用此选项。")
        }

        // === 默认 Python 路径 ===
        Comp.FormField {
            label: qsTr("默认 Python 路径")
            Layout.fillWidth: true
            helperText: qsTr("留空则使用便携 Python (python/python.exe);可填绝对路径或相对项目根的相对路径")
            Comp.PathField {
                Layout.fillWidth: true
                fileFilter: "Python (python.exe python);;All files (*)"
                path: settingsBridge.current.default_python_path || "python/python.exe"
            }
        }

        Item { Layout.fillHeight: true }  // 弹性空间
    }
}