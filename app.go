package main

import (
	"context"
	"fmt"
	"os"
	"strings"
	"syscall"
	"time"
	"unsafe"

	"github.com/wailsapp/wails/v2/pkg/runtime"
	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
)

// Service 表示一个后台服务
type Service struct {
	ID         string    `json:"id"`
	Name       string    `json:"name"`
	ExePath    string    `json:"exePath"`
	Args       string    `json:"args"`
	WorkingDir string    `json:"workingDir"`
	Status     string    `json:"status"` // "running", "stopped", "error"
	PID        int       `json:"pid"`
	AutoStart  bool      `json:"autoStart"` // 开机自启动
	CreatedAt  time.Time `json:"createdAt"`
	UpdatedAt  time.Time `json:"updatedAt"`
}

// ServiceConfig 用于创建新服务的配置
type ServiceConfig struct {
	Name       string `json:"name"`
	ExePath    string `json:"exePath"`
	Args       string `json:"args"`
	WorkingDir string `json:"workingDir"`
}

type App struct {
	ctx            context.Context
	serviceManager *WindowsServiceManager
}

func NewApp() *App {
	return &App{
		serviceManager: NewWindowsServiceManager(),
	}
}
// startup 在应用启动时调用
func (a *App) startup(ctx context.Context) {
	a.ctx = ctx
	a.serviceManager.SetContext(ctx)
	a.serviceManager.loadServices()
}

// GetServices 获取所有服务列表
func (a *App) GetServices() []*Service {
	services, err := a.serviceManager.GetServices()
	if err != nil {
		return []*Service{}
	}
	return services
}

// CreateService 创建新的服务
func (a *App) CreateService(config ServiceConfig) (*Service, error) {
	return a.serviceManager.CreateService(config)
}

// StartService 启动服务
func (a *App) StartService(serviceID string) error {
	return a.serviceManager.StartService(serviceID)
}

// StopService 停止服务
func (a *App) StopService(serviceID string) error {
	return a.serviceManager.StopService(serviceID)
}

// DeleteService 删除服务
func (a *App) DeleteService(serviceID string) error {
	return a.serviceManager.DeleteService(serviceID)
}

// SelectFile 选择文件对话框
func (a *App) SelectFile() (string, error) {
	selection, err := runtime.OpenFileDialog(a.ctx, runtime.OpenDialogOptions{
		Title: "选择可执行文件",
		Filters: []runtime.FileFilter{
			{
				DisplayName: "可执行文件 (*.exe)",
				Pattern:     "*.exe",
			},
			{
				DisplayName: "所有文件 (*.*)",
				Pattern:     "*.*",
			},
		},
	})

	if err != nil {
		return "", err
	}

	return selection, nil
}

// SelectDirectory 选择目录对话框
func (a *App) SelectDirectory() (string, error) {
	selection, err := runtime.OpenDirectoryDialog(a.ctx, runtime.OpenDialogOptions{
		Title: "选择工作目录",
	})

	if err != nil {
		return "", err
	}

	return selection, nil
}

// CheckAdminPrivileges 检查管理员权限
func (a *App) CheckAdminPrivileges() bool {
	return isUserAnAdmin()
}

func isUserAnAdmin() bool {
	if _, err := os.Open("\\\\.\\PHYSICALDRIVE0"); err == nil {
		return true
	}

	var sid *windows.SID
	err := windows.AllocateAndInitializeSid(
		&windows.SECURITY_NT_AUTHORITY,
		2,
		windows.SECURITY_BUILTIN_DOMAIN_RID,
		windows.DOMAIN_ALIAS_RID_ADMINS,
		0, 0, 0, 0, 0, 0,
		&sid,
	)
	if err != nil {
		return false
	}
	defer windows.FreeSid(sid)

	token, err := windows.OpenCurrentProcessToken()
	if err != nil {
		token, err = openCurrentThreadTokenSafe()
		if err != nil {
			return false
		}
	}
	defer token.Close()

	member, err := token.IsMember(sid)
	if err != nil {
		return false
	}

	return member
}

// openCurrentThreadTokenSafe 安全地获取当前线程的访问令牌
func openCurrentThreadTokenSafe() (windows.Token, error) {
	if err := impersonateSelf(); err != nil {
		return 0, err
	}
	defer revertToSelf()

	thread, err := getCurrentThread()
	if err != nil {
		return 0, err
	}

	var token windows.Token
	err = openThreadToken(thread, windows.TOKEN_QUERY, true, &token)
	if err != nil {
		return 0, err
	}

	return token, nil
}

// Windows API 函数声明
var (
	modadvapi32 = windows.NewLazySystemDLL("advapi32.dll")
	modkernel32 = windows.NewLazySystemDLL("kernel32.dll")

	procGetCurrentThread = modkernel32.NewProc("GetCurrentThread")
	procOpenThreadToken  = modadvapi32.NewProc("OpenThreadToken")
	procImpersonateSelf  = modadvapi32.NewProc("ImpersonateSelf")
	procRevertToSelf     = modadvapi32.NewProc("RevertToSelf")
)

// getCurrentThread 获取当前线程的伪句柄
func getCurrentThread() (windows.Handle, error) {
	r0, _, e1 := syscall.Syscall(procGetCurrentThread.Addr(), 0, 0, 0, 0)
	handle := windows.Handle(r0)
	if handle == 0 {
		if e1 != 0 {
			return 0, error(e1)
		}
		return 0, syscall.EINVAL
	}
	return handle, nil
}

// openThreadToken 打开线程访问令牌
func openThreadToken(h windows.Handle, access uint32, self bool, token *windows.Token) error {
	var _p0 uint32
	if self {
		_p0 = 1
	}
	r1, _, e1 := syscall.Syscall6(
		procOpenThreadToken.Addr(),
		4,
		uintptr(h),
		uintptr(access),
		uintptr(_p0),
		uintptr(unsafe.Pointer(token)),
		0, 0,
	)
	if r1 == 0 {
		if e1 != 0 {
			return error(e1)
		}
		return syscall.EINVAL
	}
	return nil
}

// impersonateSelf 模拟自身
func impersonateSelf() error {
	r0, _, e1 := syscall.Syscall(procImpersonateSelf.Addr(), 1, uintptr(2), 0, 0)
	if r0 == 0 {
		if e1 != 0 {
			return error(e1)
		}
		return syscall.EINVAL
	}
	return nil
}

// revertToSelf 恢复到原始安全上下文
func revertToSelf() error {
	r0, _, e1 := syscall.Syscall(procRevertToSelf.Addr(), 0, 0, 0, 0)
	if r0 == 0 {
		if e1 != 0 {
			return error(e1)
		}
		return syscall.EINVAL
	}
	return nil
}

// SetAutoStart 设置开机自启动
func (a *App) SetAutoStart(enabled bool) error {
	execPath, err := os.Executable()
	if err != nil {
		return fmt.Errorf("获取程序路径失败: %v", err)
	}

	key, err := registry.OpenKey(registry.CURRENT_USER, `SOFTWARE\Microsoft\Windows\CurrentVersion\Run`, registry.ALL_ACCESS)
	if err != nil {
		return fmt.Errorf("打开注册表失败: %v", err)
	}
	defer key.Close()

	appName := "WindowsServiceManager"

	if enabled {
		err = key.SetStringValue(appName, execPath)
		if err != nil {
			return fmt.Errorf("设置启动项失败: %v", err)
		}
	} else {
		err = key.DeleteValue(appName)
		if err != nil && err != registry.ErrNotExist {
			return fmt.Errorf("删除启动项失败: %v", err)
		}
	}

	return nil
}

// GetAutoStartStatus 获取开机自启动状态
func (a *App) GetAutoStartStatus() bool {
	key, err := registry.OpenKey(registry.CURRENT_USER, `SOFTWARE\Microsoft\Windows\CurrentVersion\Run`, registry.QUERY_VALUE)
	if err != nil {
		return false
	}
	defer key.Close()

	_, _, err = key.GetStringValue("WindowsServiceManager")
	return err == nil
}

// RestartAsAdmin 以管理员权限重启应用
func (a *App) RestartAsAdmin() error {
	exe, err := os.Executable()
	if err != nil {
		return err
	}

	cwd, err := os.Getwd()
	if err != nil {
		return err
	}

	args := strings.Join(os.Args[1:], " ")

	verbPtr, err := syscall.UTF16PtrFromString("runas")
	if err != nil {
		return err
	}
	exePtr, err := syscall.UTF16PtrFromString(exe)
	if err != nil {
		return err
	}
	cwdPtr, err := syscall.UTF16PtrFromString(cwd)
	if err != nil {
		return err
	}
	argPtr, err := syscall.UTF16PtrFromString(args)
	if err != nil {
		return err
	}

	var showCmd int32 = 1

	err = windows.ShellExecute(0, verbPtr, exePtr, argPtr, cwdPtr, showCmd)
	if err != nil {
		return err
	}

	os.Exit(0)
	return nil
}

func (a *App) ShowWindow() {
	runtime.WindowShow(a.ctx)
	runtime.WindowUnminimise(a.ctx)
	runtime.WindowCenter(a.ctx)
	runtime.WindowSetAlwaysOnTop(a.ctx, true)
	runtime.WindowSetAlwaysOnTop(a.ctx, false)
}

func (a *App) HideWindow() {
	runtime.WindowHide(a.ctx)
}

// SetServiceAutoStart 设置服务开机自启动
func (a *App) SetServiceAutoStart(serviceID string, enabled bool) error {
	return a.serviceManager.SetServiceAutoStart(serviceID, enabled)
}

// GetServiceAutoStart 获取服务开机自启动状态
func (a *App) GetServiceAutoStart(serviceID string) bool {
	return a.serviceManager.GetServiceAutoStart(serviceID)
}
