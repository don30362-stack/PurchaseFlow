import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '@/stores/auth'

import LoginView from '@/views/LoginView.vue'
import RequestsView from '@/views/RequestsView.vue'
import PurchaseRequestFormView from '@/views/PurchaseRequestFormView.vue'

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    {
      path: '/',
      redirect: '/login',
    },
    {
      path: '/login',
      name: 'login',
      component: LoginView,
    },
    {
      path: '/requests',
      name: 'requests',
      component: RequestsView,
      meta: {
        requiresAuth: true,
      },
    },
    {
      path: '/requests/new',
      name: 'request-create',
      component: PurchaseRequestFormView,
      meta: {
        requiresAuth: true,
      },
    },
    {
      path: '/requests/:id/edit',
      name: 'request-edit',
      component: PurchaseRequestFormView,
      meta: {
        requiresAuth: true,
      },
    },

  ],
})

router.beforeEach(async (to) => {
  // 公開頁面直接放行，但登入頁也需要確認登入狀態
  if (to.name !== 'login' && !to.meta.requiresAuth) {
    return true
  }

  const authStore = useAuthStore()

  try {
    await authStore.initialize()
  } catch {
    // 後端無法連線時，不允許進入受保護頁面
    // 登入頁則保持可用，讓使用者查看錯誤訊息
    return to.meta.requiresAuth
      ? { name: 'login' }
      : true
  }

  // 已登入且角色有效，不應繼續停留在登入頁
  if (to.name === 'login') {
    if (
      authStore.isAuthenticated &&
      (authStore.role === 'Employee' ||
        authStore.role === 'Manager')
    ) {
      return { name: 'requests' }
    }

    return true
  }

  if (!authStore.isAuthenticated) {
    return { name: 'login' }
  }

  // 只有 Employee 可以進入新增及編輯草稿頁面
  if (
    to.name === 'request-create' ||
    to.name === 'request-edit'
  ) {
    if (authStore.role !== 'Employee') {
      return { name: 'requests' }
    }
  }


  if (to.name === 'requests') {
    const view = to.query.view

    if (authStore.role === 'Employee') {
      if (view !== 'mine') {
        return {
          name: 'requests',
          query: { view: 'mine' },
        }
      }
    } else if (authStore.role === 'Manager') {
      if (view !== 'pending' && view !== 'reviewed') {
        return {
          name: 'requests',
          query: { view: 'pending' },
        }
      }
    } else {
      return { name: 'login' }
    }
  }

  return true
})

export default router
