package main

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/wailsapp/wails/v2/pkg/runtime"
	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/mgr"
)

// WindowsServiceManager 使用Windows Service Control Manager API管理服务
type WindowsServiceManager struct {
	mutex       sync.RWMutex
	dataFile    string
	services    map[string]*Service
	statusCache *ServiceStatusCache
	ctx         context.Context
}

// NewWindowsServiceManager 创建新的Windows服务管理器
func NewWindowsServiceManager() *WindowsServiceManager {
	cache := NewServiceStatusCache()
	cache.StartCleanupRoutine()

	return &WindowsServiceManager{
		services:    make(map[string]*Service),
		dataFile:    filepath.Join(os.TempDir(), "windows_services_data.json"),
		statusCache: cache,
	}
}

// SetContext 设置上下文用于事件发射
func (wsm *WindowsServiceManager) SetContext(ctx context.Context) {
	wsm.ctx = ctx
}

// emitServiceStatusChanged 发射服务状态变化事件
func (wsm *WindowsServiceManager) emitServiceStatusChanged(serviceID, status string, pid int) {
	if wsm.ctx != nil {
		runtime.EventsEmit(wsm.ctx, "service-status-changed", map[string]interface{}{
			"serviceId": serviceID,
			"status":    status,
			"pid":       pid,
		})
	}
}

// emitServicesUpdated 发射服务列表更新事件
func (wsm *WindowsServiceManager) emitServicesUpdated() {
	if wsm.ctx != nil {
		services := make([]*Service, 0, len(wsm.services))
		for _, service := range wsm.services {
			services = append(services, service)
		}
		runtime.EventsEmit(wsm.ctx, "services-updated", services)
	}
}

// connectSCM 连接到Windows服务控制管理器
func (wsm *WindowsServiceManager) connectSCM() (*mgr.Mgr, error) {
	return mgr.Connect()
}

// withSCM 使用SCM执行操作的辅助函数
func (wsm *WindowsServiceManager) withSCM(operation func(*mgr.Mgr) error) error {
	scm, err := wsm.connectSCM()
	if err != nil {
		return fmt.Errorf("连接服务控制管理器失败: %v", err)
	}
	defer scm.Disconnect()

	return operation(scm)
}

// waitForServiceState 等待服务达到指定状态
func (wsm *WindowsServiceManager) waitForServiceState(windowsService *mgr.Service, targetState svc.State, timeout time.Duration) error {
	deadline := time.Now().Add(timeout)

	for time.Now().Before(deadline) {
		status, err := windowsService.Query()
		if err != nil {
			return fmt.Errorf("查询服务状态失败: %v", err)
		}

		if status.State == targetState {
			return nil
		}

		if targetState == svc.Running && status.State == svc.Stopped {
			return fmt.Errorf("服务启动失败")
		}

		time.Sleep(500 * time.Millisecond)
	}

	return fmt.Errorf("等待服务状态超时")
}

// setServiceRegistryValue 通用的服务注册表值设置函数
func (wsm *WindowsServiceManager) setServiceRegistryValue(serviceName, subKey, valueName, value string) error {
	keyPath := fmt.Sprintf(`SYSTEM\CurrentControlSet\Services\%s`, serviceName)
	if subKey != "" {
		keyPath = fmt.Sprintf(`%s\%s`, keyPath, subKey)
	}

	var key registry.Key
	var err error

	if subKey != "" {
		parentKey, err := registry.OpenKey(registry.LOCAL_MACHINE, fmt.Sprintf(`SYSTEM\CurrentControlSet\Services\%s`, serviceName), registry.SET_VALUE)
		if err != nil {
			return fmt.Errorf("打开服务注册表键失败: %v", err)
		}
		defer parentKey.Close()

		key, _, err = registry.CreateKey(parentKey, subKey, registry.SET_VALUE)
		if err != nil {
			return fmt.Errorf("创建注册表子键失败: %v", err)
		}
	} else {
		key, err = registry.OpenKey(registry.LOCAL_MACHINE, keyPath, registry.SET_VALUE)
		if err != nil {
			return fmt.Errorf("打开服务注册表键失败: %v", err)
		}
	}
	defer key.Close()

	err = key.SetStringValue(valueName, value)
	if err != nil {
		return fmt.Errorf("设置注册表值失败: %v", err)
	}

	return nil
}

// setServiceWorkingDirectory 通过注册表设置服务的工作目录
func (wsm *WindowsServiceManager) setServiceWorkingDirectory(serviceName, workingDir string) error {
	return wsm.setServiceRegistryValue(serviceName, "Parameters", "AppDirectory", workingDir)
}

// setServiceImagePathDirect 直接设置服务的ImagePath值
func (wsm *WindowsServiceManager) setServiceImagePathDirect(serviceName, imagePath string) error {
	return wsm.setServiceRegistryValue(serviceName, "", "ImagePath", imagePath)
}

// createServiceWrapper 设置内置服务包装器（使用当前程序+参数模式）
func (wsm *WindowsServiceManager) createServiceWrapper(serviceName, exePath, args, workingDir string) (string, error) {
	currentExe, err := os.Executable()
	if err != nil {
		return "", fmt.Errorf("获取当前可执行文件路径失败: %v", err)
	}

	err = wsm.storeServiceConfigInRegistry(serviceName, exePath, args, workingDir)
	if err != nil {
		return "", fmt.Errorf("存储服务配置失败: %v", err)
	}

	return fmt.Sprintf(`"%s" --service-wrapper %s`, currentExe, serviceName), nil
}

// storeServiceConfigInRegistry 将服务配置存储到注册表
func (wsm *WindowsServiceManager) storeServiceConfigInRegistry(serviceName, exePath, args, workingDir string) error {
	if err := wsm.setServiceRegistryValue(serviceName, "Parameters", "ExePath", exePath); err != nil {
		return fmt.Errorf("设置ExePath失败: %v", err)
	}

	if args != "" {
		if err := wsm.setServiceRegistryValue(serviceName, "Parameters", "Args", args); err != nil {
			return fmt.Errorf("设置Args失败: %v", err)
		}
	}

	if workingDir != "" {
		if err := wsm.setServiceRegistryValue(serviceName, "Parameters", "WorkingDir", workingDir); err != nil {
			return fmt.Errorf("设置WorkingDir失败: %v", err)
		}
	}

	return nil
}

// GetServices 获取所有由我们管理的服务
func (wsm *WindowsServiceManager) GetServices() ([]*Service, error) {
	wsm.mutex.RLock()
	defer wsm.mutex.RUnlock()

	var services []*Service

	err := wsm.withSCM(func(scm *mgr.Mgr) error {
		services = make([]*Service, 0, len(wsm.services))
		for _, service := range wsm.services {
			status, pid := wsm.getServiceRealTimeStatus(scm, service.ID)
			service.Status = status
			service.PID = pid
			service.UpdatedAt = time.Now()
			services = append(services, service)
		}
		return nil
	})

	if err != nil {
		return nil, err
	}

	go wsm.saveServices()

	return services, nil
}

// CreateService 使用Windows SCM创建系统服务
func (wsm *WindowsServiceManager) CreateService(config ServiceConfig) (*Service, error) {
	wsm.mutex.Lock()
	defer wsm.mutex.Unlock()

	if _, err := os.Stat(config.ExePath); os.IsNotExist(err) {
		return nil, fmt.Errorf("可执行文件不存在: %s", config.ExePath)
	}

	serviceName := wsm.generateServiceName(config.Name)

	if _, exists := wsm.services[serviceName]; exists {
		return nil, fmt.Errorf("服务名称已存在: %s", serviceName)
	}

	workingDir := config.WorkingDir
	if workingDir == "" {
		workingDir = filepath.Dir(config.ExePath)
	}

	var service *Service

	err := wsm.withSCM(func(scm *mgr.Mgr) error {
		serviceConfig := mgr.Config{
			ServiceType:  windows.SERVICE_WIN32_OWN_PROCESS,
			StartType:    mgr.StartAutomatic,
			ErrorControl: mgr.ErrorNormal,
			DisplayName:  config.Name,
			Description:  fmt.Sprintf("由Windows服务管理器创建的服务: %s", config.Name),
		}

		binaryPath := config.ExePath
		if config.Args != "" {
			binaryPath = fmt.Sprintf("\"%s\" %s", config.ExePath, config.Args)
		}

		windowsService, err := scm.CreateService(serviceName, binaryPath, serviceConfig)
		if err != nil {
			return fmt.Errorf("创建Windows服务失败: %v", err)
		}
		defer windowsService.Close()

		wrapperPath, err := wsm.createServiceWrapper(serviceName, config.ExePath, config.Args, workingDir)
		if err != nil {
			windowsService.Delete()
			return fmt.Errorf("创建服务包装器失败: %v", err)
		}

		err = wsm.setServiceImagePathDirect(serviceName, wrapperPath)
		if err != nil {
			windowsService.Delete()
			return fmt.Errorf("设置服务路径失败: %v", err)
		}

		err = wsm.setServiceWorkingDirectory(serviceName, workingDir)
		if err != nil {
			fmt.Printf("警告：设置工作目录失败: %v\n", err)
		}

		service = &Service{
			ID:         serviceName,
			Name:       config.Name,
			ExePath:    config.ExePath,
			Args:       config.Args,
			WorkingDir: workingDir,
			Status:     "stopped",
			PID:        0,
			AutoStart:  false,
			CreatedAt:  time.Now(),
			UpdatedAt:  time.Now(),
		}

		return nil
	})

	if err != nil {
		return nil, err
	}

	wsm.services[serviceName] = service
	wsm.saveServices()
	
	// 发射服务列表更新事件
	wsm.emitServicesUpdated()
	
	// 自动启动服务
	go func() {
		time.Sleep(1 * time.Second)
		wsm.StartService(serviceName)
	}()

	return service, nil
}

// StartService 启动Windows服务
func (wsm *WindowsServiceManager) StartService(serviceID string) error {
	wsm.mutex.Lock()
	defer wsm.mutex.Unlock()

	service, exists := wsm.services[serviceID]
	if !exists {
		return fmt.Errorf("服务不存在: %s", serviceID)
	}

	return wsm.withSCM(func(scm *mgr.Mgr) error {
		windowsService, err := scm.OpenService(serviceID)
		if err != nil {
			return fmt.Errorf("打开服务失败: %v", err)
		}
		defer windowsService.Close()

		status, err := windowsService.Query()
		if err != nil {
			return fmt.Errorf("查询服务状态失败: %v", err)
		}

		if status.State == svc.Running {
			return fmt.Errorf("服务已经在运行")
		}

		err = windowsService.Start()
		if err != nil {
			return fmt.Errorf("启动服务失败: %v", err)
		}

		err = wsm.waitForServiceState(windowsService, svc.Running, 30*time.Second)
		if err != nil {
			service.Status = "error"
			service.UpdatedAt = time.Now()
			wsm.saveServices()
			return err
		}

		status, _ = windowsService.Query()
		service.Status = "running"
		service.PID = int(status.ProcessId)
		service.UpdatedAt = time.Now()
		wsm.statusCache.Set(serviceID, "running", int(status.ProcessId))
		wsm.saveServices()
		
		// 发射状态变化事件
		wsm.emitServiceStatusChanged(serviceID, "running", int(status.ProcessId))

		return nil
	})
}

// StopService 停止Windows服务
func (wsm *WindowsServiceManager) StopService(serviceID string) error {
	wsm.mutex.Lock()
	defer wsm.mutex.Unlock()

	service, exists := wsm.services[serviceID]
	if !exists {
		return fmt.Errorf("服务不存在: %s", serviceID)
	}

	return wsm.withSCM(func(scm *mgr.Mgr) error {
		windowsService, err := scm.OpenService(serviceID)
		if err != nil {
			return fmt.Errorf("打开服务失败: %v", err)
		}
		defer windowsService.Close()

		status, err := windowsService.Query()
		if err != nil {
			return fmt.Errorf("查询服务状态失败: %v", err)
		}

		if status.State == svc.Stopped {
			service.Status = "stopped"
			service.PID = 0
			service.UpdatedAt = time.Now()
			wsm.saveServices()
			return nil
		}

		_, err = windowsService.Control(svc.Stop)
		if err != nil {
			return fmt.Errorf("发送停止信号失败: %v", err)
		}

		err = wsm.waitForServiceState(windowsService, svc.Stopped, 30*time.Second)
		if err != nil {
			return err
		}

		service.Status = "stopped"
		service.PID = 0
		service.UpdatedAt = time.Now()
		wsm.statusCache.Set(serviceID, "stopped", 0)
		wsm.saveServices()
		
		// 发射状态变化事件
		wsm.emitServiceStatusChanged(serviceID, "stopped", 0)

		return nil
	})
}

// DeleteService 删除Windows服务
func (wsm *WindowsServiceManager) DeleteService(serviceID string) error {
	wsm.mutex.Lock()
	defer wsm.mutex.Unlock()

	_, exists := wsm.services[serviceID]
	if !exists {
		return fmt.Errorf("服务不存在: %s", serviceID)
	}

	return wsm.withSCM(func(scm *mgr.Mgr) error {
		windowsService, err := scm.OpenService(serviceID)
		if err != nil {
			return fmt.Errorf("打开服务失败: %v", err)
		}
		defer windowsService.Close()

		status, err := windowsService.Query()
		if err == nil && status.State != svc.Stopped {
			windowsService.Control(svc.Stop)

			wsm.waitForServiceState(windowsService, svc.Stopped, 30*time.Second)
		}

		err = windowsService.Delete()
		if err != nil {
			return fmt.Errorf("删除服务失败: %v", err)
		}

		delete(wsm.services, serviceID)
		wsm.statusCache.Remove(serviceID)
		wsm.saveServices()
		
		// 发射服务列表更新事件
		wsm.emitServicesUpdated()

		return nil
	})
}

// getServiceRealTimeStatus 获取服务实时状态（使用缓存优化）
func (wsm *WindowsServiceManager) getServiceRealTimeStatus(scm *mgr.Mgr, serviceName string) (string, int) {
	if cachedStatus, found := wsm.statusCache.Get(serviceName); found {
		return cachedStatus.Status, cachedStatus.PID
	}

	windowsService, err := scm.OpenService(serviceName)
	if err != nil {
		wsm.statusCache.Set(serviceName, "error", 0)
		return "error", 0
	}
	defer windowsService.Close()

	status, err := windowsService.Query()
	if err != nil {
		wsm.statusCache.Set(serviceName, "error", 0)
		return "error", 0
	}

	var statusStr string
	var pid int

	switch status.State {
	case svc.Running:
		statusStr = "running"
		pid = int(status.ProcessId)
	case svc.Stopped:
		statusStr = "stopped"
		pid = 0
	case svc.StartPending:
		statusStr = "starting"
		pid = 0
	case svc.StopPending:
		statusStr = "stopping"
		pid = int(status.ProcessId)
	default:
		statusStr = "error"
		pid = 0
	}

	// 更新缓存
	wsm.statusCache.Set(serviceName, statusStr, pid)
	return statusStr, pid
}

// generateServiceName 生成唯一的服务名称
func (wsm *WindowsServiceManager) generateServiceName(displayName string) string {
	cleanName := strings.Map(func(r rune) rune {
		if (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') {
			return r
		}
		return '_'
	}, displayName)

	return fmt.Sprintf("WSM_%s_%d", cleanName, time.Now().Unix())
}

// saveServices 保存服务数据到文件
func (wsm *WindowsServiceManager) saveServices() {
	data, err := json.MarshalIndent(wsm.services, "", "  ")
	if err != nil {
		return
	}
	os.WriteFile(wsm.dataFile, data, 0644)
}

// loadServices 从文件加载服务数据
func (wsm *WindowsServiceManager) loadServices() {
	if _, err := os.Stat(wsm.dataFile); os.IsNotExist(err) {
		return
	}

	data, err := os.ReadFile(wsm.dataFile)
	if err != nil {
		return
	}

	json.Unmarshal(data, &wsm.services)
}

// SetServiceAutoStart 设置服务开机自启动
func (wsm *WindowsServiceManager) SetServiceAutoStart(serviceID string, enabled bool) error {
	wsm.mutex.Lock()
	defer wsm.mutex.Unlock()

	service, exists := wsm.services[serviceID]
	if !exists {
		return fmt.Errorf("服务不存在: %s", serviceID)
	}

	return wsm.withSCM(func(scm *mgr.Mgr) error {
		windowsService, err := scm.OpenService(serviceID)
		if err != nil {
			return fmt.Errorf("打开服务失败: %v", err)
		}
		defer windowsService.Close()

		// 获取当前服务配置
		config, err := windowsService.Config()
		if err != nil {
			return fmt.Errorf("获取服务配置失败: %v", err)
		}

		// 修改启动类型
		if enabled {
			config.StartType = mgr.StartAutomatic
		} else {
			config.StartType = mgr.StartManual
		}

		// 更新服务配置
		err = windowsService.UpdateConfig(config)
		if err != nil {
			return fmt.Errorf("更新服务配置失败: %v", err)
		}

		// 更新内存中的服务信息
		service.AutoStart = enabled
		service.UpdatedAt = time.Now()
		wsm.saveServices()

		return nil
	})
}

// GetServiceAutoStart 获取服务开机自启动状态
func (wsm *WindowsServiceManager) GetServiceAutoStart(serviceID string) bool {
	wsm.mutex.RLock()
	defer wsm.mutex.RUnlock()

	service, exists := wsm.services[serviceID]
	if !exists {
		return false
	}

	return service.AutoStart
}
