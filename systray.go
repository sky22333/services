package main

import (
	"github.com/getlantern/systray"
	"github.com/wailsapp/wails/v2/pkg/runtime"
	"os"
)

// SystrayManager 管理系统托盘
type SystrayManager struct {
	app      *App
	trayIcon []byte
	quitCh   chan struct{}
}

// NewSystrayManager 创建新的系统托盘管理器
func NewSystrayManager(app *App, trayIconData []byte) *SystrayManager {
	return &SystrayManager{
		app:      app,
		trayIcon: trayIconData,
		quitCh:   make(chan struct{}),
	}
}

// Start 启动系统托盘
func (s *SystrayManager) Start() {
	go func() {
		defer func() {
			if r := recover(); r != nil {
				println("系统托盘启动失败:", r)
			}
		}()

		systray.Run(s.onReady, s.onExit)
	}()
}

// onReady 托盘初始化完成时调用
func (s *SystrayManager) onReady() {
	if len(s.trayIcon) > 0 {
		systray.SetIcon(s.trayIcon)
	} else {
		systray.SetIcon([]byte{})
	}

	systray.SetTitle("Windows Service Manager")
	systray.SetTooltip("Windows 服务管理器 - 右键显示菜单")

	mShow := systray.AddMenuItem("显示窗口", "显示主窗口")
	systray.AddSeparator()
	mExit := systray.AddMenuItem("退出程序", "退出应用程序")

	go func() {
		for {
			select {
			case <-mShow.ClickedCh:
				s.app.ShowWindow()

			case <-mExit.ClickedCh:
				s.ExitApp()
				return

			case <-s.quitCh:
				return
			}
		}
	}()
}

// ExitApp 退出应用程序
func (s *SystrayManager) ExitApp() {
	select {
	case s.quitCh <- struct{}{}:
	default:
	}

	systray.Quit()

	runtime.Quit(s.app.ctx)

	os.Exit(0)
}

// onExit 托盘退出时调用
func (s *SystrayManager) onExit() {
	// 清理工作在Cleanup()中处理
}

// Cleanup 清理系统托盘资源
func (s *SystrayManager) Cleanup() {
	select {
	case s.quitCh <- struct{}{}:
	default:
	}

	systray.Quit()
}
