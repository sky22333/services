package main

import (
	"fmt"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
	"unsafe"

	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
)

// EnvironmentManager 环境变量管理器
type EnvironmentManager struct{}

func NewEnvironmentManager() *EnvironmentManager {
	return &EnvironmentManager{}
}

// AddSystemEnvironmentVariable 添加系统级环境变量
func (em *EnvironmentManager) AddSystemEnvironmentVariable(varName, varValue string) error {
	key, err := registry.OpenKey(registry.LOCAL_MACHINE, 
		`SYSTEM\CurrentControlSet\Control\Session Manager\Environment`, 
		registry.ALL_ACCESS)
	if err != nil {
		return fmt.Errorf("无法打开系统环境变量注册表 (需要管理员权限): %v", err)
	}
	defer key.Close()

	var valueType uint32
	if strings.ToUpper(varName) == "PATH" || strings.Contains(varValue, "%") {
		valueType = registry.EXPAND_SZ
	} else {
		valueType = registry.SZ
	}

	// 如果是PATH变量，需要特殊处理
	if strings.ToUpper(varName) == "PATH" {
		var existingPath string
		var readErr error
		
		existingPath, _, readErr = key.GetStringValue("PATH")
		if readErr != nil && readErr != registry.ErrNotExist {
			return fmt.Errorf("无法读取现有PATH变量: %v", readErr)
		}
		
		if existingPath != "" {
			pathEntries := strings.Split(existingPath, ";")
			for _, entry := range pathEntries {
				if strings.EqualFold(strings.TrimSpace(entry), strings.TrimSpace(varValue)) {
					return fmt.Errorf("PATH中已存在该路径: %s", varValue)
				}
			}
		}
		
		if existingPath != "" {
			if !strings.HasSuffix(existingPath, ";") {
				varValue = existingPath + ";" + varValue
			} else {
				varValue = existingPath + varValue
			}
		}
	}

	// 设置注册表值
	if valueType == registry.EXPAND_SZ {
		err = key.SetExpandStringValue(varName, varValue)
	} else {
		err = key.SetStringValue(varName, varValue)
	}
	
	if err != nil {
		return fmt.Errorf("无法设置环境变量: %v", err)
	}

	// 立即通知系统环境变量已更改
	err = em.broadcastEnvironmentChange()
	if err != nil {
		return fmt.Errorf("环境变量设置成功，但通知系统失败: %v", err)
	}

	return nil
}

// AddPathVariable 专门用于添加PATH环境变量
func (em *EnvironmentManager) AddPathVariable(pathValue string) error {
	pathValue = strings.Trim(pathValue, "\"")
	
	if !filepath.IsAbs(pathValue) {
		return fmt.Errorf("必须提供绝对路径")
	}

	if strings.HasSuffix(strings.ToLower(pathValue), ".exe") {
		pathValue = filepath.Dir(pathValue)
	}

	return em.AddSystemEnvironmentVariable("PATH", pathValue)
}

// broadcastEnvironmentChange 广播环境变量更改消息
func (em *EnvironmentManager) broadcastEnvironmentChange() error {
	const (
		HWND_BROADCAST   = 0xffff
		WM_SETTINGCHANGE = 0x001A
		SMTO_ABORTIFHUNG = 0x0002
	)

	user32 := windows.NewLazySystemDLL("user32.dll")
	sendMessageTimeoutW := user32.NewProc("SendMessageTimeoutW")

	environmentPtr, _ := syscall.UTF16PtrFromString("Environment")

	ret, _, err := sendMessageTimeoutW.Call(
		uintptr(HWND_BROADCAST),
		uintptr(WM_SETTINGCHANGE),
		0,
		uintptr(unsafe.Pointer(environmentPtr)),
		uintptr(SMTO_ABORTIFHUNG),
		uintptr(5000), // 5秒超时
		0,
	)

	if ret == 0 {
		return fmt.Errorf("广播环境变量更改失败: %v", err)
	}

	return nil
}

// OpenSystemEnvironmentSettings 打开系统环境变量设置
func (em *EnvironmentManager) OpenSystemEnvironmentSettings() error 
	cmd := exec.Command("rundll32.exe", "sysdm.cpl,EditEnvironmentVariables")
	return cmd.Start()
}

// ValidatePathExists 验证路径是否存在
func (em *EnvironmentManager) ValidatePathExists(path string) bool {
	path = strings.Trim(path, "\"")
	if _, err := windows.GetFileAttributes(windows.StringToUTF16Ptr(path)); err != nil {
		return false
	}
	return true
}

// GetSystemEnvironmentVariable 获取系统环境变量值
func (em *EnvironmentManager) GetSystemEnvironmentVariable(varName string) (string, error) {
	key, err := registry.OpenKey(registry.LOCAL_MACHINE, 
		`SYSTEM\CurrentControlSet\Control\Session Manager\Environment`, 
		registry.QUERY_VALUE)
	if err != nil {
		return "", fmt.Errorf("无法打开系统环境变量注册表: %v", err)
	}
	defer key.Close()

	value, _, err := key.GetStringValue(varName)
	if err != nil {
		if err == registry.ErrNotExist {
			return "", fmt.Errorf("环境变量不存在: %s", varName)
		}
		return "", fmt.Errorf("无法读取环境变量: %v", err)
	}

	return value, nil
}

// DiagnoseEnvironmentAccess 诊断环境变量访问权限
func (em *EnvironmentManager) DiagnoseEnvironmentAccess() (map[string]interface{}, error) {
	result := make(map[string]interface{})
	
	key, err := registry.OpenKey(registry.LOCAL_MACHINE, 
		`SYSTEM\CurrentControlSet\Control\Session Manager\Environment`, 
		registry.QUERY_VALUE)
	if err != nil {
		result["registry_read"] = false
		result["registry_read_error"] = err.Error()
	} else {
		result["registry_read"] = true
		key.Close()
	}
	
	key, err = registry.OpenKey(registry.LOCAL_MACHINE, 
		`SYSTEM\CurrentControlSet\Control\Session Manager\Environment`, 
		registry.SET_VALUE)
	if err != nil {
		result["registry_write"] = false
		result["registry_write_error"] = err.Error()
	} else {
		result["registry_write"] = true
		key.Close()
	}
	
	key, err = registry.OpenKey(registry.LOCAL_MACHINE, 
		`SYSTEM\CurrentControlSet\Control\Session Manager\Environment`, 
		registry.ALL_ACCESS)
	if err != nil {
		result["registry_full"] = false
		result["registry_full_error"] = err.Error()
	} else {
		result["registry_full"] = true
		key.Close()
	}
	
	pathValue, err := em.GetSystemEnvironmentVariable("PATH")
	if err != nil {
		result["path_read"] = false
		result["path_read_error"] = err.Error()
	} else {
		result["path_read"] = true
		result["path_length"] = len(pathValue)
	}
	
	return result, nil
}