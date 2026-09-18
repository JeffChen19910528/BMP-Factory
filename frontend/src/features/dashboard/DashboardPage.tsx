import { ReloadOutlined } from '@ant-design/icons';
import { Button, Card, Col, Empty, List, Row, Space, Statistic, Tag, Typography } from 'antd';
import { useNavigate } from 'react-router-dom';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { useAuthStore } from '../../stores/authStore';
import { useTranslation } from '../../i18n/LanguageContext';
import { useDashboard } from './hooks';

// Phase 7.1 — Dashboard Foundation. Every number here comes directly from GET /api/dashboard
// (DashboardQueryService) — this component never computes a KPI, an "overdue" flag, or a "pending"
// count itself; it only renders what the backend already decided, per Part 15/29's explicit
// "frontend is UX only" boundary. Deliberately no charts/trends/analytics (Part 16 — those are
// Phase 7.4).
export function DashboardPage() {
  const navigate = useNavigate();
  const query = useDashboard();
  const isAdministrator = useAuthStore((state) => state.user?.roles.includes('Administrator') ?? false);
  const { t } = useTranslation();

  if (query.isError) {
    return <ApiErrorAlert error={query.error} title={t('dashboard.title')} />;
  }

  const data = query.data;

  return (
    <div>
      <Space style={{ marginBottom: 16, width: '100%', justifyContent: 'space-between' }}>
        <Typography.Title level={3} style={{ margin: 0 }}>
          {t('dashboard.title')}
        </Typography.Title>
        <Button icon={<ReloadOutlined />} onClick={() => query.refetch()} loading={query.isFetching}>
          {t('dashboard.refresh')}
        </Button>
      </Space>

      {/* Part 10: this label is UX only — the numbers themselves are already scoped server-side
          regardless of what this banner says; a normal user simply never receives system-wide
          counts from the API in the first place. */}
      <Typography.Paragraph type="secondary">
        {isAdministrator ? t('dashboard.adminBanner') : t('dashboard.personalBanner')}
      </Typography.Paragraph>

      <Row gutter={16}>
        <Col xs={24} sm={8}>
          <Card loading={query.isLoading} onClick={() => navigate('/tasks')} style={{ cursor: 'pointer' }}>
            <Statistic title={t('dashboard.myTasks')} value={data?.myTasks.total ?? 0} />
          </Card>
        </Col>
        <Col xs={24} sm={8}>
          <Card loading={query.isLoading} onClick={() => navigate('/approvals')} style={{ cursor: 'pointer' }}>
            <Statistic title={t('dashboard.pendingApprovals')} value={data?.pendingApprovals.pending ?? 0} />
          </Card>
        </Col>
        <Col xs={24} sm={8}>
          <Card loading={query.isLoading} onClick={() => navigate('/tasks')} style={{ cursor: 'pointer' }}>
            <Statistic
              title={t('dashboard.overdueTasks')}
              value={data?.myTasks.overdue ?? 0}
              valueStyle={data && data.myTasks.overdue > 0 ? { color: '#cf1322' } : undefined}
            />
          </Card>
        </Col>
      </Row>

      <Row gutter={16} style={{ marginTop: 16 }}>
        <Col xs={24} md={12}>
          <Card title={t('dashboard.processOverview')} loading={query.isLoading}>
            <Row gutter={16}>
              <Col span={8}>
                <Statistic title={t('dashboard.running')} value={data?.processOverview.running ?? 0} />
              </Col>
              <Col span={8}>
                <Statistic title={t('dashboard.completed')} value={data?.processOverview.completed ?? 0} />
              </Col>
              <Col span={8}>
                <Statistic title={t('dashboard.rejected')} value={data?.processOverview.rejected ?? 0} />
              </Col>
            </Row>
          </Card>
        </Col>
        <Col xs={24} md={12}>
          <Card title={t('dashboard.slaOverview')} loading={query.isLoading} style={{ marginTop: 16 }}>
            <Row gutter={16}>
              <Col span={6}>
                <Statistic title={t('dashboard.active')} value={data?.sla.active ?? 0} />
              </Col>
              <Col span={6}>
                <Statistic title={t('dashboard.warning')} value={data?.sla.warning ?? 0} valueStyle={data && data.sla.warning > 0 ? { color: '#d48806' } : undefined} />
              </Col>
              <Col span={6}>
                <Statistic title={t('dashboard.overdue')} value={data?.sla.overdue ?? 0} valueStyle={data && data.sla.overdue > 0 ? { color: '#cf1322' } : undefined} />
              </Col>
              <Col span={6}>
                <Statistic title={t('dashboard.completed')} value={data?.sla.completed ?? 0} />
              </Col>
            </Row>
          </Card>
        </Col>
      </Row>

      <Card title={t('dashboard.recentActivity')} loading={query.isLoading} style={{ marginTop: 16 }}>
        {data && data.recentActivity.length === 0 ? (
          <Empty description={t('dashboard.noRecentActivity')} />
        ) : (
          <List
            dataSource={data?.recentActivity ?? []}
            renderItem={(item) => (
              <List.Item
                key={`${item.timestamp}-${item.action}`}
                actions={
                  item.processInstanceId
                    ? [
                        <Button key="view" type="link" size="small" onClick={() => navigate('/instances')}>
                          {t('dashboard.view')}
                        </Button>,
                      ]
                    : undefined
                }
              >
                <List.Item.Meta
                  title={
                    <Space>
                      <Tag>{item.action}</Tag>
                      {item.description}
                    </Space>
                  }
                  description={new Date(item.timestamp).toLocaleString()}
                />
              </List.Item>
            )}
          />
        )}
      </Card>
    </div>
  );
}
