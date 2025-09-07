import { useState, useEffect, useCallback, useMemo, memo } from 'react';
import {
  Button,
  Input,
  Table,
  TableHeader,
  TableRow,
  TableHeaderCell,
  TableBody,
  TableCell,
  Dialog,
  DialogTrigger,
  DialogSurface,
  DialogTitle,
  DialogContent,
  DialogActions,
  DialogBody,
  Field,
  Text,
  Badge,
  Toast,
  Toaster,
  useToastController,
  ToastTitle,
  Switch,
  Tooltip
} from '@fluentui/react-components';
import {
  Add24Regular,
  Play24Regular,
  Stop24Regular,
  Delete24Regular,
  DocumentFolder24Regular,
  Settings24Regular,
  ArrowClockwise24Regular
} from '@fluentui/react-icons';
import { EventsOn, EventsOff } from '../wailsjs/runtime/runtime';
import './App.css';
import { 
  GetServices, 
  CreateService, 
  StartService, 
  StopService, 
  DeleteService,
  SelectFile,
  SelectDirectory,
  CheckAdminPrivileges,
  SetAutoStart,
  GetAutoStartStatus,
  SetServiceAutoStart,
  RestartAsAdmin
} from "../wailsjs/go/main/App";

// 服务行组件，使用memo优化
const ServiceRow = memo(({ service, onStart, onStop, onDelete, onAutoStartToggle }) => {
  const handleStart = useCallback(() => onStart(service.id), [service.id, onStart]);
  const handleStop = useCallback(() => onStop(service.id), [service.id, onStop]);
  const handleDelete = useCallback(() => onDelete(service.id), [service.id, onDelete]);
  const handleAutoStartToggle = useCallback((checked) => onAutoStartToggle(service.id, checked), [service.id, onAutoStartToggle]);

  return (
    <TableRow key={service.id} className="win11-table-row">
      <TableCell>
        <Text weight="semibold" size="300">{service.name}</Text>
        <br />
        <Text size="200" style={{ color: '#666' }}>
          PID: {service.pid || 'N/A'}
        </Text>
      </TableCell>
      <TableCell>
        <div className={`service-status ${service.status}`}>
          <div style={{
            width: '6px',
            height: '6px',
            borderRadius: '50%',
            backgroundColor: service.status === 'running' ? '#107c10' : 
                           service.status === 'error' ? '#c42b1c' : '#605e5c'
          }}></div>
          {service.status === 'running' ? '运行中' : 
           service.status === 'error' ? '错误' : '已停止'}
        </div>
      </TableCell>
      <TableCell>
        <Text size="200" style={{ wordBreak: 'break-all' }}>
          {service.exePath}
        </Text>
        {service.args && (
          <>
            <br />
            <Text size="100" style={{ color: '#666', fontStyle: 'italic' }}>
              参数: {service.args}
            </Text>
          </>
        )}
      </TableCell>
      <TableCell>
        <Switch
          checked={service.autoStart || false}
          onChange={(_, data) => handleAutoStartToggle(data.checked)}
          className="win11-switch"
        />
      </TableCell>
      <TableCell>
        <div style={{ display: 'flex', gap: '6px', alignItems: 'center' }}>
          {service.status === 'stopped' ? (
            <Tooltip content="启动服务" relationship="label">
              <Button
                size="small"
                appearance="subtle"
                icon={<Play24Regular />}
                onClick={handleStart}
                className="win11-button"
              />
            </Tooltip>
          ) : (
            <Tooltip content="停止服务" relationship="label">
              <Button
                size="small"
                appearance="secondary"
                icon={<Stop24Regular />}
                onClick={handleStop}
                className="win11-button"
              />
            </Tooltip>
          )}
          
          <Tooltip content="删除服务" relationship="label">
            <Button
              size="small"
              appearance="subtle"
              icon={<Delete24Regular />}
              onClick={handleDelete}
              className="win11-button win11-delete-button"
            />
          </Tooltip>
        </div>
      </TableCell>
    </TableRow>
  );
});

ServiceRow.displayName = 'ServiceRow';

function App() {
  const [services, setServices] = useState([]);
  const [isAddDialogOpen, setIsAddDialogOpen] = useState(false);
  const [isSettingsDialogOpen, setIsSettingsDialogOpen] = useState(false);
  const [isDeleteDialogOpen, setIsDeleteDialogOpen] = useState(false);
  const [serviceToDelete, setServiceToDelete] = useState(null);
  const [adminPrivileges, setAdminPrivileges] = useState(false);
  const [autoStart, setAutoStart] = useState(false);
  const [showAdminWarning, setShowAdminWarning] = useState(false);
  const [newService, setNewService] = useState({
    name: '',
    exePath: '',
    args: '',
    workingDir: ''
  });
  
  const { dispatchToast } = useToastController();

  const showToast = useCallback((title, message, intent = 'success') => {
    dispatchToast(
      <Toast>
        <ToastTitle>{title}</ToastTitle>
        {message && <Text>{message}</Text>}
      </Toast>,
      { intent, timeout: 3000 }
    );
  }, [dispatchToast]);

  useEffect(() => {
    loadServices();
    checkAdminRights();
    checkAutoStartStatus();
    
    // 监听服务状态变化事件
    EventsOn('service-status-changed', (data) => {
      setServices(prev => prev.map(service => 
        service.id === data.serviceId 
          ? { ...service, status: data.status, pid: data.pid }
          : service
      ));
    });
    
    // 监听服务列表更新事件
    EventsOn('services-updated', (serviceList) => {
      setServices(serviceList || []);
    });
    
    return () => {
      EventsOff('service-status-changed');
      EventsOff('services-updated');
    };
  }, []);

  const checkAdminRights = useCallback(async () => {
    try {
      const isAdmin = await CheckAdminPrivileges();
      setAdminPrivileges(isAdmin);
      if (!isAdmin) {
        setShowAdminWarning(true);
      }
    } catch (error) {
      console.error('检查权限失败:', error);
    }
  }, []);

  const checkAutoStartStatus = useCallback(async () => {
    try {
      const status = await GetAutoStartStatus();
      setAutoStart(status);
    } catch (error) {
      console.error('检查开机自启状态失败:', error);
    }
  }, []);

  const handleAppAutoStartToggle = useCallback(async (enabled) => {
    try {
      await SetAutoStart(enabled);
      setAutoStart(enabled);
      showToast('成功', `开机自启动已${enabled ? '启用' : '禁用'}`);
    } catch (error) {
      showToast('错误', '设置开机自启动失败: ' + error, 'error');
    }
  }, [showToast]);

  const handleRestartAsAdmin = useCallback(async () => {
    try {
      await RestartAsAdmin();
    } catch (error) {
      showToast('错误', '以管理员身份重启失败: ' + error, 'error');
    }
  }, [showToast]);

  const loadServices = useCallback(async () => {
    try {
      const serviceList = await GetServices();
      setServices(serviceList || []);
    } catch (error) {
      showToast('错误', '加载服务列表失败: ' + error, 'error');
    }
  }, [showToast]);

  const handleCreateService = useCallback(async () => {
    if (!newService.name || !newService.exePath) {
      showToast('验证错误', '请填写服务名称和可执行文件路径', 'error');
      return;
    }

    try {
      await CreateService(newService);
      showToast('成功', '服务创建成功');
      setIsAddDialogOpen(false);
      setNewService({
        name: '',
        exePath: '',
        args: '',
        workingDir: ''
      });
      loadServices();
    } catch (error) {
      showToast('错误', '创建服务失败: ' + error, 'error');
    }
  }, [newService, showToast, loadServices]);

  const handleStartService = useCallback(async (serviceId) => {
    try {
      await StartService(serviceId);
      showToast('成功', '服务启动成功');
      loadServices();
    } catch (error) {
      showToast('错误', '启动服务失败: ' + error, 'error');
    }
  }, [showToast, loadServices]);

  const handleStopService = useCallback(async (serviceId) => {
    try {
      await StopService(serviceId);
      showToast('成功', '服务停止成功');
      loadServices();
    } catch (error) {
      showToast('错误', '停止服务失败: ' + error, 'error');
    }
  }, [showToast, loadServices]);

  const handleDeleteService = useCallback((serviceId) => {
    const service = services.find(s => s.id === serviceId);
    setServiceToDelete(service);
    setIsDeleteDialogOpen(true);
  }, [services]);

  const confirmDeleteService = useCallback(async () => {
    if (!serviceToDelete) return;
    
    try {
      await DeleteService(serviceToDelete.id);
      showToast('成功', '服务删除成功');
      loadServices();
    } catch (error) {
      showToast('错误', '删除服务失败: ' + error, 'error');
    } finally {
      setIsDeleteDialogOpen(false);
      setServiceToDelete(null);
    }
  }, [serviceToDelete, showToast, loadServices]);

  const handleAutoStartToggle = useCallback(async (serviceId, enabled) => {
    try {
      await SetServiceAutoStart(serviceId, enabled);
      showToast('成功', enabled ? '已启用开机自启' : '已禁用开机自启');
      loadServices();
    } catch (error) {
      showToast('错误', '设置开机自启失败: ' + error, 'error');
    }
  }, [showToast, loadServices]);

  const handleSelectFile = useCallback(async () => {
    try {
      const filePath = await SelectFile();
      if (filePath) {
        setNewService(prev => ({ ...prev, exePath: filePath }));
      }
    } catch (error) {
      showToast('错误', '选择文件失败: ' + error, 'error');
    }
  }, [showToast]);

  const handleSelectDirectory = useCallback(async () => {
    try {
      const dirPath = await SelectDirectory();
      if (dirPath) {
        setNewService(prev => ({ ...prev, workingDir: dirPath }));
      }
    } catch (error) {
      showToast('错误', '选择目录失败: ' + error, 'error');
    }
  }, [showToast]);


  const columns = useMemo(() => [
    { columnKey: 'name', label: '服务名称' },
    { columnKey: 'status', label: '状态' },
    { columnKey: 'exePath', label: '程序路径' },
    { columnKey: 'autoStart', label: '开机自启' },
    { columnKey: 'actions', label: '操作' }
  ], []);

  const serviceStats = useMemo(() => ({
    total: services.length,
    running: services.filter(s => s.status === 'running').length,
    stopped: services.filter(s => s.status === 'stopped').length
  }), [services]);

  return (
    <>
      <Toaster />
      <div className="app-container">
        <div className="header">
          <Text size="400" weight="semibold">Windows 服务管理器</Text>
          <div style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: '10px' }}>
            {!adminPrivileges && (
              <Badge color="warning" appearance="filled" className="win11-badge">非管理员模式</Badge>
            )}
            <Button 
              appearance="subtle" 
              icon={<Settings24Regular />}
              onClick={() => setIsSettingsDialogOpen(true)}
              className="win11-button"
            >
              设置
            </Button>
            <Dialog open={isAddDialogOpen} onOpenChange={(_, data) => setIsAddDialogOpen(data.open)}>
              <DialogTrigger disableButtonEnhancement>
                <Button appearance="primary" icon={<Add24Regular />} className="win11-button">
                  添加服务
                </Button>
              </DialogTrigger>
              <DialogSurface className="win11-dialog">
                <DialogBody>
                  <DialogTitle>添加新服务</DialogTitle>
                  <DialogContent>
                    <div style={{ display: 'flex', flexDirection: 'column', gap: '16px' }}>
                      <Field label="服务名称" required>
                        <Input
                          value={newService.name}
                          onChange={(e) => setNewService(prev => ({ ...prev, name: e.target.value }))}
                          placeholder="输入服务名称"
                          className="win11-input"
                        />
                      </Field>
                      
                      <Field label="可执行文件路径" required>
                        <div style={{ display: 'flex', gap: '8px' }}>
                          <Input
                            value={newService.exePath}
                            onChange={(e) => setNewService(prev => ({ ...prev, exePath: e.target.value }))}
                            placeholder="输入程序路径"
                            style={{ flex: 1 }}
                            className="win11-input"
                          />
                          <Button 
                            icon={<DocumentFolder24Regular />} 
                            onClick={handleSelectFile}
                            className="win11-button"
                          >
                            选择
                          </Button>
                        </div>
                      </Field>
                      
                      <Field label="启动参数">
                        <Input
                          value={newService.args}
                          onChange={(e) => setNewService(prev => ({ ...prev, args: e.target.value }))}
                          placeholder="输入启动参数（可选）"
                          className="win11-input"
                        />
                      </Field>
                      
                      <Field label="工作目录">
                        <div style={{ display: 'flex', gap: '8px' }}>
                          <Input
                            value={newService.workingDir}
                            onChange={(e) => setNewService(prev => ({ ...prev, workingDir: e.target.value }))}
                            placeholder="工作目录（留空使用程序目录）"
                            style={{ flex: 1 }}
                            className="win11-input"
                          />
                          <Button 
                            icon={<DocumentFolder24Regular />} 
                            onClick={handleSelectDirectory}
                            className="win11-button"
                          >
                            选择
                          </Button>
                        </div>
                      </Field>
                      
                      <Field label="服务启动">
                        <Text size="300" style={{ 
                          color: '#666', 
                          fontStyle: 'italic',
                          padding: '8px 12px',
                          backgroundColor: '#f3f4f6',
                          borderRadius: '6px',
                          border: '1px solid #e5e7eb'
                        }}>
                          💡 服务创建后将自动启动并运行
                        </Text>
                      </Field>
                    </div>
                  </DialogContent>
                  <DialogActions>
                    <DialogTrigger disableButtonEnhancement>
                      <Button appearance="secondary" className="win11-button">取消</Button>
                    </DialogTrigger>
                    <Button appearance="primary" onClick={handleCreateService} className="win11-button">
                      创建服务
                    </Button>
                  </DialogActions>
                </DialogBody>
              </DialogSurface>
            </Dialog>
          </div>
        </div>

        {/* 权限警告对话框 */}
        <Dialog open={showAdminWarning} modalType="alert">
          <DialogSurface className="win11-dialog">
            <DialogBody>
              <DialogTitle>权限警告</DialogTitle>
              <DialogContent>
                <div style={{ display: 'flex', flexDirection: 'column', gap: '16px', textAlign: 'center' }}>
                  <Text size="400" weight="semibold" style={{ color: '#d13438' }}>
                    当前没有管理员权限，无法使用服务管理功能！
                  </Text>
                  <Text size="300">
                    请使用管理员权限重新启动程序以获得完整功能。
                  </Text>
                </div>
              </DialogContent>
              <DialogActions>
                <Button 
                  appearance="primary" 
                  onClick={handleRestartAsAdmin}
                  className="win11-button"
                >
                  以管理员身份重启
                </Button>
                <Button 
                  appearance="secondary" 
                  onClick={() => setShowAdminWarning(false)}
                  className="win11-button"
                >
                  暂时忽略
                </Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>

        {/* 设置对话框 */}
        <Dialog open={isSettingsDialogOpen} onOpenChange={(_, data) => setIsSettingsDialogOpen(data.open)}>
          <DialogSurface className="win11-dialog">
            <DialogBody>
              <DialogTitle>应用设置</DialogTitle>
              <DialogContent>
                <div style={{ display: 'flex', flexDirection: 'column', gap: '18px' }}>
                  <Field label="权限管理">
                    <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
                      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                        <Text>当前权限状态</Text>
                        <Badge 
                          color={adminPrivileges ? "success" : "warning"} 
                          appearance="filled"
                          className="win11-badge"
                        >
                          {adminPrivileges ? "管理员权限" : "普通权限"}
                        </Badge>
                      </div>
                      {!adminPrivileges && (
                        <Button 
                          appearance="primary" 
                          size="small"
                          onClick={handleRestartAsAdmin}
                          className="win11-button"
                        >
                          以管理员身份重启
                        </Button>
                      )}
                    </div>
                  </Field>

                  <Field label="开机自启动">
                    <div style={{ 
                      display: 'flex', 
                      justifyContent: 'space-between', 
                      alignItems: 'center',
                      padding: '12px 16px',
                      backgroundColor: 'rgba(255, 255, 255, 0.5)',
                      borderRadius: '12px',
                      backdropFilter: 'blur(10px)'
                    }}>
                      <Text>为此程序添加开机自启动项</Text>
                      <Switch
                        checked={autoStart}
                        onChange={(_, data) => handleAppAutoStartToggle(data.checked)}
                      />
                    </div>
                  </Field>

                  <Field label="应用信息">
                    <div style={{ 
                      display: 'flex', 
                      flexDirection: 'column', 
                      gap: '8px',
                      padding: '16px',
                      backgroundColor: 'rgba(255, 255, 255, 0.5)',
                      borderRadius: '12px',
                      backdropFilter: 'blur(10px)'
                    }}>
                      <Text size="300" weight="semibold">Windows Service Manager</Text>
                      <Text size="200" style={{ color: '#666' }}>现代化 Windows 服务管理工具</Text>
                      <Text size="200" style={{ color: '#666' }}>使程序以后台服务的形式运行</Text>
                      <Text size="200" style={{ color: '#666' }}>项目地址: https://github.com/sky22333/services</Text>
                    </div>
                  </Field>
                </div>
              </DialogContent>
              <DialogActions>
                <Button 
                  appearance="primary" 
                  onClick={() => setIsSettingsDialogOpen(false)}
                  className="win11-button"
                >
                  关闭
                </Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>

        <div className="main-content">
          <div className="content-area">
            <div style={{ 
              display: 'flex', 
              justifyContent: 'space-between', 
              alignItems: 'center',
              marginBottom: '16px'
            }}>
              <Text size="300" weight="semibold">服务列表</Text>
              <Button 
                appearance="subtle" 
                icon={<ArrowClockwise24Regular />}
                onClick={loadServices}
                className="win11-button"
              >
                刷新
              </Button>
            </div>
            
            {services.length === 0 ? (
              <div className="empty-state">
                <div className="empty-state-icon">⚙️</div>
                <div className="empty-state-text">
                  暂无服务<br />
                  点击右上角"添加服务"按钮开始创建服务
                </div>
              </div>
            ) : (
              <Table className="win11-table slide-in">
                <TableHeader className="win11-table-header">
                  <TableRow>
                    {columns.map(col => (
                      <TableHeaderCell key={col.columnKey}>
                        <Text weight="semibold">{col.label}</Text>
                      </TableHeaderCell>
                    ))}
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {services.map(service => (
                    <ServiceRow
                      key={service.id}
                      service={service}
                      onStart={handleStartService}
                      onStop={handleStopService}
                      onDelete={handleDeleteService}
                      onAutoStartToggle={handleAutoStartToggle}
                    />
                  ))}
                </TableBody>
              </Table>
            )}
          </div>
        </div>

        <div className="status-bar">
          <Text size="200">
            总计服务: {serviceStats.total} | 
            运行中: {serviceStats.running} | 
            已停止: {serviceStats.stopped}
            {adminPrivileges ? ' | 管理员权限' : ' | 普通权限'}
          </Text>
        </div>
      </div>

      {/* 删除确认对话框 */}
      <Dialog open={isDeleteDialogOpen} onOpenChange={(_, data) => setIsDeleteDialogOpen(data.open)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>确认删除服务</DialogTitle>
            <DialogContent>
              <Text>
                确定要删除服务 "{serviceToDelete?.name}" 吗？
              </Text>
              <Text style={{ marginTop: '8px', color: '#d13438' }}>
                服务将被删除！
              </Text>
            </DialogContent>
            <DialogActions>
              <Button 
                appearance="secondary" 
                onClick={() => setIsDeleteDialogOpen(false)}
              >
                取消
              </Button>
              <Button 
                appearance="primary" 
                onClick={confirmDeleteService}
                style={{ backgroundColor: '#d13438', borderColor: '#d13438' }}
              >
                删除
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </>
  );
}

export default memo(App);