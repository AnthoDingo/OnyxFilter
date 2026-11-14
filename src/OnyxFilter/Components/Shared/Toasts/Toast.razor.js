/**
 * Displays a toast notification and notifies .NET once it is hidden.
 * @param {HTMLElement} element Toast element
 * @param {Partial<bootstrap.Toast.Options>} options Bootstrap toast options
 * @param {{ invokeMethodAsync: (methodName: string, ...args: any[]) => Promise<any> }} dotNetRef .NET reference to the Toast component
 * @returns {void}
 */
export const displayToast = (element, options, dotNetRef) => {
    element.addEventListener('hidden.bs.toast', () => {
        bootstrap.Toast.getInstance(element)?.dispose();

        dotNetRef.invokeMethodAsync('HideAsync');
    }, {once: true});

    bootstrap.Toast.getOrCreateInstance(element, options).show();
};
