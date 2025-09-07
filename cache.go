package main

import (
	"sync"
	"time"
)

// ServiceStatusCache 服务状态缓存，减少SCM查询次数
type ServiceStatusCache struct {
	cache map[string]*CachedServiceStatus
	mutex sync.RWMutex
	ttl   time.Duration
}

// CachedServiceStatus 缓存的服务状态
type CachedServiceStatus struct {
	Status    string
	PID       int
	Timestamp time.Time
}

// NewServiceStatusCache 创建新的服务状态缓存
func NewServiceStatusCache() *ServiceStatusCache {
	return &ServiceStatusCache{
		cache: make(map[string]*CachedServiceStatus),
		ttl:   5 * time.Second, // 5秒缓存有效期
	}
}

// Get 获取缓存的服务状态
func (cache *ServiceStatusCache) Get(serviceName string) (*CachedServiceStatus, bool) {
	cache.mutex.RLock()
	defer cache.mutex.RUnlock()

	status, exists := cache.cache[serviceName]
	if !exists {
		return nil, false
	}

	if time.Since(status.Timestamp) > cache.ttl {
		return nil, false
	}

	return status, true
}

// Set 设置服务状态缓存
func (cache *ServiceStatusCache) Set(serviceName string, status string, pid int) {
	cache.mutex.Lock()
	defer cache.mutex.Unlock()

	cache.cache[serviceName] = &CachedServiceStatus{
		Status:    status,
		PID:       pid,
		Timestamp: time.Now(),
	}
}

// Remove 移除服务状态缓存
func (cache *ServiceStatusCache) Remove(serviceName string) {
	cache.mutex.Lock()
	defer cache.mutex.Unlock()

	delete(cache.cache, serviceName)
}

// Clear 清空所有缓存
func (cache *ServiceStatusCache) Clear() {
	cache.mutex.Lock()
	defer cache.mutex.Unlock()

	cache.cache = make(map[string]*CachedServiceStatus)
}

// CleanExpired 清理过期的缓存项
func (cache *ServiceStatusCache) CleanExpired() {
	cache.mutex.Lock()
	defer cache.mutex.Unlock()

	now := time.Now()
	for serviceName, status := range cache.cache {
		if now.Sub(status.Timestamp) > cache.ttl {
			delete(cache.cache, serviceName)
		}
	}
}

// StartCleanupRoutine 启动定期清理过期缓存的协程
func (cache *ServiceStatusCache) StartCleanupRoutine() {
	go func() {
		ticker := time.NewTicker(30 * time.Second)
		defer ticker.Stop()

		for {
			select {
			case <-ticker.C:
				cache.CleanExpired()
			}
		}
	}()
}
