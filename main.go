package main

import (
	"context"
	"embed"
	"log"
	"os"

	"github.com/wailsapp/wails/v2"
	"github.com/wailsapp/wails/v2/pkg/options"
	"github.com/wailsapp/wails/v2/pkg/options/assetserver"
	"github.com/wailsapp/wails/v2/pkg/options/windows"
	"github.com/wailsapp/wails/v2/pkg/runtime"
)

//go:embed all:frontend/dist
var assets embed.FS

//go:embed embed/icon.ico
var trayIcon []byte

func main() {
	if isWrapper, serviceName := IsServiceWrapperMode(); isWrapper {
		config, err := LoadServiceConfigFromRegistry(serviceName)
		if err != nil {
			log.Fatalf("加载服务配置失败: %v", err)
		}

		err = RunAsWindowsService(serviceName, *config)
		if err != nil {
			log.Fatalf("运行Windows服务失败: %v", err)
		}
		return
	}

	app := NewApp()

	systrayManager := NewSystrayManager(app, trayIcon)

	err := wails.Run(&options.App{
		Title:     "Windows Service Manager",
		Width:     900,
		Height:    650,
		MinWidth:  750,
		MinHeight: 500,
		AssetServer: &assetserver.Options{
			Assets: assets,
		},
		BackgroundColour: &options.RGBA{R: 239, G: 244, B: 249, A: 1},
		SingleInstanceLock: &options.SingleInstanceLock{
			UniqueId: "Windows-Service-Manager",
			OnSecondInstanceLaunch: func(data options.SecondInstanceData) {
				runtime.Show(app.ctx)
				runtime.WindowUnminimise(app.ctx)
				runtime.BrowserOpenURL(app.ctx, "")
			},
		},
		OnStartup: func(ctx context.Context) {
			app.startup(ctx)
		},
		OnDomReady: func(ctx context.Context) {
			go systrayManager.Start()
		},
		OnBeforeClose: func(ctx context.Context) (prevent bool) {
			runtime.WindowHide(ctx)
			return true
		},
		OnShutdown: func(ctx context.Context) {
			systrayManager.Cleanup()
			os.Exit(0)
		},
		Bind: []interface{}{
			app,
		},
		WindowStartState: options.Normal,
		Windows: &windows.Options{
			WebviewIsTransparent: false,
			WindowIsTranslucent:  false,
			DisablePinchZoom:     false,
		},
	})

	if err != nil {
		println("Error:", err.Error())
	}
}
