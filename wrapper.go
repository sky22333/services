package main

import (
	"fmt"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
	"time"

	"golang.org/x/sys/windows/registry"
	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/debug"
)

// EmbeddedServiceWrapper 内置服务包装器
type EmbeddedServiceWrapper struct {
	serviceName string
	config      ServiceConfig
	process     *exec.Cmd
	isRunning   bool
}

// NewEmbeddedServiceWrapper 创建内置服务包装器
func NewEmbeddedServiceWrapper(serviceName string, config ServiceConfig) *EmbeddedServiceWrapper {
	return &EmbeddedServiceWrapper{
		serviceName: serviceName,
		config:      config,
		isRunning:   false,
	}
}

// Execute 实现Windows服务接口
func (esw *EmbeddedServiceWrapper) Execute(args []string, r <-chan svc.ChangeRequest, s chan<- svc.Status) (bool, uint32) {
	log.Printf("EmbeddedServiceWrapper 开始执行服务: %s", esw.serviceName)

	s <- svc.Status{State: svc.StartPending}

	err := esw.startTargetProcess()
	if err != nil {
		log.Printf("启动目标程序失败: %v", err)
		s <- svc.Status{State: svc.Stopped}
		return false, 1
	}

	s <- svc.Status{State: svc.Running, Accepts: svc.AcceptStop | svc.AcceptShutdown}
	log.Printf("服务已启动，目标程序PID: %d", esw.process.Process.Pid)

	go esw.monitorTargetProcess()

	for {
		select {
		case c := <-r:
			switch c.Cmd {
			case svc.Stop, svc.Shutdown:
				log.Printf("服务接收到停止信号: %s", esw.serviceName)
				s <- svc.Status{State: svc.StopPending}
				esw.stopTargetProcess()
				s <- svc.Status{State: svc.Stopped}
				return false, 0
			case svc.Interrogate:
				s <- c.CurrentStatus
			default:
				log.Printf("服务接收到未知命令: %v", c.Cmd)
			}
		default:
			if !esw.isRunning {
				log.Printf("目标程序已退出，停止服务: %s", esw.serviceName)
				s <- svc.Status{State: svc.Stopped}
				return false, 0
			}
			time.Sleep(1 * time.Second)
		}
	}
}

// startTargetProcess 启动目标程序
func (esw *EmbeddedServiceWrapper) startTargetProcess() error {
	var args []string
	if esw.config.Args != "" {
		args = strings.Fields(esw.config.Args)
	}

	esw.process = exec.Command(esw.config.ExePath, args...)

	workingDir := esw.config.WorkingDir
	if workingDir == "" {
		workingDir = filepath.Dir(esw.config.ExePath)
	}
	esw.process.Dir = workingDir

	esw.process.SysProcAttr = &syscall.SysProcAttr{
		HideWindow: true,
	}

	err := esw.process.Start()
	if err != nil {
		return fmt.Errorf("启动目标程序失败: %v", err)
	}

	esw.isRunning = true
	log.Printf("目标程序已启动: %s，PID: %d", esw.config.ExePath, esw.process.Process.Pid)
	return nil
}

// stopTargetProcess 停止目标程序
func (esw *EmbeddedServiceWrapper) stopTargetProcess() {
	if esw.process != nil && esw.isRunning {
		log.Printf("正在停止目标程序，PID: %d", esw.process.Process.Pid)

		esw.process.Process.Kill()

		esw.process.Wait()
		esw.isRunning = false
		log.Printf("目标程序已停止")
	}
}

// monitorTargetProcess 监控目标程序
func (esw *EmbeddedServiceWrapper) monitorTargetProcess() {
	if esw.process != nil {
		esw.process.Wait()
		esw.isRunning = false
		log.Printf("目标程序已退出: %s", esw.config.ExePath)
	}
}

// RunAsWindowsService 将程序作为Windows服务运行（内置包装器模式）
func RunAsWindowsService(serviceName string, config ServiceConfig) error {
	wrapper := NewEmbeddedServiceWrapper(serviceName, config)

	isService, err := svc.IsWindowsService()
	if err != nil {
		return fmt.Errorf("检查服务状态失败: %v", err)
	}

	if isService {
		log.Printf("作为Windows服务运行: %s", serviceName)
		err = svc.Run(serviceName, wrapper)
		if err != nil {
			return fmt.Errorf("服务运行失败: %v", err)
		}
	} else {
		log.Printf("调试模式运行: %s", serviceName)
		err = debug.Run(serviceName, wrapper)
		if err != nil {
			return fmt.Errorf("调试运行失败: %v", err)
		}
	}

	return nil
}

// IsServiceWrapperMode 检查是否以服务包装器模式运行
func IsServiceWrapperMode() (bool, string) {
	args := os.Args
	if len(args) >= 3 && args[1] == "--service-wrapper" {
		return true, args[2] // 返回服务名
	}
	return false, ""
}

// LoadServiceConfigFromRegistry 从注册表加载服务配置
func LoadServiceConfigFromRegistry(serviceName string) (*ServiceConfig, error) {
	keyPath := fmt.Sprintf(`SYSTEM\CurrentControlSet\Services\%s\Parameters`, serviceName)

	key, err := registry.OpenKey(registry.LOCAL_MACHINE, keyPath, registry.READ)
	if err != nil {
		return nil, fmt.Errorf("打开服务配置注册表失败: %v", err)
	}
	defer key.Close()

	exePath, _, err := key.GetStringValue("ExePath")
	if err != nil {
		return nil, fmt.Errorf("读取ExePath失败: %v", err)
	}

	args, _, err := key.GetStringValue("Args")
	if err != nil {
		args = ""
	}

	workingDir, _, err := key.GetStringValue("WorkingDir")
	if err != nil {
		workingDir = ""
	}

	displayName, _, err := key.GetStringValue("DisplayName")
	if err != nil {
		displayName = serviceName
	}

	return &ServiceConfig{
		Name:       displayName,
		ExePath:    exePath,
		Args:       args,
		WorkingDir: workingDir,
	}, nil
}
