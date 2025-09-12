## Windows Service Manager

现代化的 Windows 后台服务管理工具，支持NSSM的大部分功能，并且拥有美观的可视化操作界面，是NSSM完美替代品。

## 功能特性

### 🚀 核心功能
- **服务管理**: 将任意`exe`程序注册为后台服务运行
- **隐藏运行**: 支持隐藏终端窗口运行服务
- **启动参数**: 支持为服务添加启动参数
- **工作目录**: 支持自定义服务工作目录
- **进程控制**: 启动、停止、开机自启
- **支持多服务**: 支持管理多个服务，退出GUI程序不影响后台服务

## 技术架构

- **后端**: Go 1.24
- **前端**: React + TypeScript
- **UI框架**: Fluent UI React Components
- **桌面框架**: Wails 2.10+
- **系统托盘**: Systray
- **图标**: Fluent UI Icons

## 系统要求

- Windows 10 +
- Windows Server 2016 +
- WebView2 Runtime (通常已预装)

## 构建说明

### 环境准备
```bash
# 安装 Wails CLI
go install github.com/wailsapp/wails/v2/cmd/wails@latest

# 安装 Node.js 依赖
cd frontend && npm install
```

### 生产构建
```bash
wails build
```

### 开发模式
```bash
wails dev
```

## 界面预览

![主界面](/.github/demo/demo1.jpg)

![添加服务](/.github/demo/demo2.jpg)